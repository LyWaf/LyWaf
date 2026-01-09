using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Utils;
using Newtonsoft.Json.Linq;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Policy
{
    public class LyxProbingRequestFactory : IProbingRequestFactory
    {
        public HttpRequestMessage CreateRequest(ClusterModel cluster, DestinationModel destination)
        {
            var active = cluster.Config.HealthCheck?.Active;

            if (active != null)
            {
                var metadata = cluster.Config.Metadata;
                var method = (metadata?.GetValueOrDefault("LyxActiveHealth.Method") ?? "GET") switch
                {
                    "POST" => HttpMethod.Post,
                    _ => HttpMethod.Get,
                };
                var body = metadata?.GetValueOrDefault("LyxActiveHealth.Body");
                var activePath = active.Path ?? "/";
                var needSub = destination.Config.Address.EndsWith('/') && activePath.StartsWith('/');
                var fullUri = needSub ? destination.Config.Address + activePath.TrimStart('/') : destination.Config.Address + active.Path;
                var probeUri = new Uri(fullUri + "?" + active.Query ?? "");
                var request = new HttpRequestMessage(method, probeUri) { Version = HttpVersion.Version11 };
                if (body != null)
                {
                    var json = CommonUtil.SafeJson(body);
                    // 成功转成JSON
                    if (json.Count != 0)
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                    }
                }
                return request;
            }
            else
            {
                var probeUri = new Uri(destination.Config.Address);
                return new HttpRequestMessage(HttpMethod.Get, probeUri) { Version = HttpVersion.Version11 };
            }
        }
    }

    public class LyxActiveHealthPolicy(IDestinationHealthUpdater healthUpdater) : IActiveHealthCheckPolicy
    {
        public string Name => "LyxActiveHealth";

        private readonly Dictionary<string, int> FailTimes = [];
        private readonly Dictionary<string, int> PassTimes = [];
        private readonly Dictionary<string, HashSet<int>> CacheStatusCodes = [];
        private readonly IDestinationHealthUpdater _healthUpdater = healthUpdater;

        private const int DEFAULT_PASS_TIMES = 2;
        private const int DEFAULT_FAIL_TIMES = 2;
        private bool IsInStatus(string availdCode, int code)
        {
            if (CacheStatusCodes.TryGetValue(availdCode, out var val))
            {
                return val.Contains(code);
            }
            var codes = availdCode.Split(",");
            HashSet<int> hash = [];
            foreach (var k in codes)
            {
                if (k.Contains("xx"))
                {
                    var s = Convert.ToInt32(k.TrimEnd('x'));
                    if (s > 0)
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            hash.Add(s * 100 + i);
                        }
                    }
                }
                else if (k.Contains('x'))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        var s = Convert.ToInt32(k.Replace("x", i.ToString()));
                        hash.Add(s);
                    }
                }
                else
                {
                    var s = Convert.ToInt32(k);
                    hash.Add(s);
                }
            }
            CacheStatusCodes[availdCode] = hash;
            return hash.Contains(code);
        }

        private async Task<bool> IsSucc(IReadOnlyDictionary<string, string>? metadata, DestinationProbingResult result)
        {
            if (result.Response == null)
            {
                return false;
            }
            if (metadata != null)
            {
                if (metadata.TryGetValue("LyxActiveHealth.AvalidCode", out var val))
                {
                    if (!IsInStatus(val, (int)result.Response!.StatusCode))
                    {
                        return false;
                    }
                }
                if (metadata.TryGetValue("LyxActiveHealth.AvalidContent", out val))
                {
                    if (!metadata.TryGetValue("LyxActiveHealth.ContentCheck", out var method))
                    {
                        method = "Contains";
                    }

                    var content = await result.Response!.Content.ReadAsStringAsync();
                    switch (method)
                    {
                        case "Contains":
                            if (!content.Contains(val))
                            {
                                return false;
                            }
                            break;
                        case "Match":
                            if (val != content)
                            {
                                return false;
                            }
                            break;
                        case "JSON":
                            {
                                JObject contentObj = CommonUtil.SafeJson(content);
                                JObject valObj = CommonUtil.SafeJson(val);
                                foreach (var property in valObj.Properties())
                                {
                                    if (!contentObj.ContainsKey(property.Name))
                                    {
                                        return false;
                                    }
                                    else if (!JToken.DeepEquals(contentObj[property.Name], property.Value))
                                    {
                                        return false;
                                    }
                                }
                                return true;
                            }
                        case "JSONM":
                            {
                                JObject contentObj = CommonUtil.SafeJson(content);
                                JObject valObj = CommonUtil.SafeJson(val);
                                return CommonUtil.ContainsAllProperties(contentObj, valObj);
                            }
                    }
                    if (val != content)
                    {
                        return false;
                    }
                }
                if (metadata.TryGetValue("LyxActiveHealth.AvalidHeaders", out val))
                {
                    var l = val.Split(";");
                    foreach (var a in l)
                    {
                        var sub = a.Split("=");
                        if (sub.Length != 2)
                        {
                            continue;
                        }
                        if (!result.Response.Headers.TryGetValues(sub[0], out var vals))
                        {
                            return false;
                        }
                        if (!vals.Contains(sub[1]))
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            else
            {
                return result.Response!.IsSuccessStatusCode;
            }
        }

        private static int GetAtleastPassTimes(IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata == null)
            {
                return DEFAULT_PASS_TIMES;
            }
            if (metadata.TryGetValue("LyxActiveHealth.Passes", out var val))
            {
                var num = Convert.ToInt32(val);
                return num <= 0 ? DEFAULT_PASS_TIMES : num;
            }
            else
            {
                return DEFAULT_PASS_TIMES;
            }
        }

        private static int GetAtleastFailTimes(IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata == null)
            {
                return DEFAULT_FAIL_TIMES;
            }
            if (metadata.TryGetValue("LyxActiveHealth.Fails", out var val))
            {
                var num = Convert.ToInt32(val);
                return num <= 0 ? DEFAULT_FAIL_TIMES : num;
            }
            else
            {
                return DEFAULT_FAIL_TIMES;
            }
        }

        public async void ProbingCompleted(ClusterState cluster, IReadOnlyList<DestinationProbingResult> probingResults)
        {
            var metadata = cluster.Model.Config.Metadata;
            if (probingResults.Count == 0)
            {
                return;
            }
            var passTimes = GetAtleastPassTimes(metadata);
            var failTimes = GetAtleastFailTimes(metadata);
            var newHealthStates = new NewActiveDestinationHealth[probingResults.Count];
            for (var i = 0; i < probingResults.Count; i++)
            {
                var address = probingResults[i].Destination.Model.Config.Address;
                var succ = await IsSucc(metadata, probingResults[i]);

                if (succ)
                {
                    // 成功时递增成功次数
                    var now = PassTimes.GetValueOrDefault(address) + 1;
                    PassTimes[address] = now;
                    if (now >= passTimes)
                    {
                        // 达到成功次数阈值，清除失败计数
                        FailTimes[address] = 0;
                    }
                }
                else
                {
                    // 失败时递增失败次数
                    var now = FailTimes.GetValueOrDefault(address) + 1;
                    FailTimes[address] = now;
                    if (now >= failTimes)
                    {
                        // 达到失败次数阈值，清除成功计数
                        PassTimes[address] = 0;
                    }
                }

                // 确保字典中有对应的键
                if (!PassTimes.ContainsKey(address))
                {
                    PassTimes[address] = 0;
                }
                if (!FailTimes.ContainsKey(address))
                {
                    FailTimes[address] = 0;
                }

                if (PassTimes[address] >= passTimes)
                {
                    newHealthStates[i] = new NewActiveDestinationHealth(probingResults[i].Destination, DestinationHealth.Healthy);
                }
                else if (FailTimes[address] >= failTimes)
                {
                    newHealthStates[i] = new NewActiveDestinationHealth(probingResults[i].Destination, DestinationHealth.Unhealthy);
                }
                else
                {
                    newHealthStates[i] = new NewActiveDestinationHealth(probingResults[i].Destination, DestinationHealth.Unknown);
                }
            }

            // 在循环外部一次性更新所有健康状态
            _healthUpdater.SetActive(cluster, newHealthStates);
        }
    }


}