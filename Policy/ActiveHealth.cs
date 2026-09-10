using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using LyWaf.Utils;
using LyWaf.Services.Metrics;
using Newtonsoft.Json.Linq;
using NLog;
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
                var method = new HttpMethod(metadata?.GetValueOrDefault("LyxActiveHealth.Method") ?? "GET");
                var body = metadata?.GetValueOrDefault("LyxActiveHealth.Body");
                var address = destination.Config.Health ?? destination.Config.Address;
                var activePath = active.Path ?? "/";
                var probeUri = new Uri(address.TrimEnd('/') + "/" + activePath.TrimStart('/') +
                    (string.IsNullOrEmpty(active.Query) ? "" : "?" + active.Query.TrimStart('?')));
                var request = new HttpRequestMessage(method, probeUri) { Version = HttpVersion.Version11 };
                request.Options.Set(HealthProbeRequestOptions.IsProbe, true);
                if (!string.IsNullOrEmpty(body))
                {
                    if (metadata?.GetValueOrDefault("LyxActiveHealth.ContentType") is { } contentType)
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, contentType);
                        return request;
                    }
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
                var request = new HttpRequestMessage(HttpMethod.Get, probeUri) { Version = HttpVersion.Version11 };
                request.Options.Set(HealthProbeRequestOptions.IsProbe, true);
                return request;
            }
        }
    }

    public class LyxActiveHealthPolicy(
        IDestinationHealthUpdater healthUpdater,
        PrometheusMetrics metrics) : IActiveHealthCheckPolicy
    {
        public string Name => "LyxActiveHealth";

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        // Destination instances belong to one cluster; weak keys release removed destinations.
        private sealed class ProbeCounter
        {
            public int Passes;
            public int Fails;
            public DestinationHealth State;
        }
        private readonly ConditionalWeakTable<DestinationState, ProbeCounter> counters = new();
        private readonly IDestinationHealthUpdater _healthUpdater = healthUpdater;
        private readonly PrometheusMetrics _metrics = metrics;

        private const int DEFAULT_PASS_TIMES = 2;
        private const int DEFAULT_FAIL_TIMES = 2;
        private bool IsInStatus(string availdCode, int code)
        {
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
            return hash.Contains(code);
        }

        private async Task<bool> IsSucc(IReadOnlyDictionary<string, string>? metadata, DestinationProbingResult result)
        {
            if (result.Response == null)
            {
                return false;
            }
            if (metadata?.ContainsKey("LyxActiveHealth.AvalidCode") != true && !result.Response.IsSuccessStatusCode)
                return false;
            if (metadata != null)
            {
                if (metadata.TryGetValue("LyxActiveHealth.AvalidCode", out var val))
                {
                    if (!HealthStatusCodes.Matches(val, (int)result.Response!.StatusCode))
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

                    string content;
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await result.Response.Content.LoadIntoBufferAsync(65536, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                        content = await result.Response.Content.ReadAsStringAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 读取响应内容失败（连接中断、超时等）
                        _logger.Warn(ex, "健康检查读取响应内容失败");
                        return false;
                    }
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
                                JObject contentObj = JObject.Parse(content);
                                JObject valObj = JObject.Parse(val);
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
                                JObject contentObj = JObject.Parse(content);
                                JObject valObj = JObject.Parse(val);
                                return CommonUtil.ContainsAllProperties(contentObj, valObj);
                            }
                        default:
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
                var num = int.TryParse(val, out var parsed) ? parsed : DEFAULT_PASS_TIMES;
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
                var num = int.TryParse(val, out var parsed) ? parsed : DEFAULT_FAIL_TIMES;
                return num <= 0 ? DEFAULT_FAIL_TIMES : num;
            }
            else
            {
                return DEFAULT_FAIL_TIMES;
            }
        }

        public void ProbingCompleted(ClusterState cluster, IReadOnlyList<DestinationProbingResult> probingResults)
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
                bool succ;
                try
                {
                    // YARP's callback is synchronous: finish consuming the bounded body before it returns.
                    succ = IsSucc(metadata, probingResults[i]).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "健康检查结果处理失败: {0}/{1}", cluster.ClusterId, probingResults[i].Destination.DestinationId);
                    succ = false;
                }
                var counter = counters.GetOrCreateValue(probingResults[i].Destination);
                lock (counter)
                {
                    if (succ)
                    {
                        counter.Fails = 0;
                        if (counter.Passes < passTimes) counter.Passes++;
                        if (counter.Passes >= passTimes) counter.State = DestinationHealth.Healthy;
                    }
                    else
                    {
                        counter.Passes = 0;
                        if (counter.Fails < failTimes) counter.Fails++;
                        if (counter.Fails >= failTimes) counter.State = DestinationHealth.Unhealthy;
                    }
                    newHealthStates[i] = new(probingResults[i].Destination, counter.State);
                    _metrics.SetUpstreamHealth(cluster.ClusterId, address,
                        counter.State == DestinationHealth.Healthy ? 1 : counter.State == DestinationHealth.Unhealthy ? 0 : -1);
                }
            }

            // 在循环外部一次性更新所有健康状态
            _healthUpdater.SetActive(cluster, newHealthStates);
        }
    }


}
