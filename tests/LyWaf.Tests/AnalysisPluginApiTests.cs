using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class AnalysisPluginApiTests
{
    [Fact]
    public async Task ProxyPreservesGroupedRuleCrudAndPaging()
    {
        await using var host = await Host.Start();
        var added = await host.Post("cc/advanced/add", new
        {
            name = "grouped", type = "FrequentAccess", action = "Block", groupOperator = "And",
            conditionGroups = new[] { new { @operator = "Or", conditions = new[] {
                new { target = "UrlPath", @operator = "Contains", values = new[] { "/api" } }
            } } }, period = 10, threshold = 20
        });
        var id = added.GetProperty("ruleId").GetString()!;
        var detail = (await host.Get("cc/advanced/" + id)).GetProperty("rule");
        Assert.Equal("And", detail.GetProperty("groupOperator").GetString());
        Assert.Single(detail.GetProperty("conditionGroups").EnumerateArray());
        Assert.Equal("Or", detail.GetProperty("conditionGroups")[0].GetProperty("operator").GetString());
        await host.Post("cc/advanced/add", new { name = "other" });
        var page = await host.Get("cc/advanced?page=1&pageSize=1&search=grouped");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(id, page.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal(2, (await host.Get("cc/advanced/type/FrequentAccess")).GetProperty("total").GetInt32());
        Assert.True((await host.Get("cc/advanced/enums")).TryGetProperty("ruleTypes", out _));
        await host.Post("cc/advanced/update", new { id, name = "updated", threshold = 30 });
        Assert.Equal(30, (await host.Get("cc/advanced/" + id)).GetProperty("rule").GetProperty("threshold").GetInt32());
        await host.Post("cc/advanced/toggle", new { ruleId = id, enabled = false });
        Assert.False((await host.Get("cc/advanced/" + id)).GetProperty("rule").GetProperty("enabled").GetBoolean());
        await host.Post("cc/advanced/remove", new { ruleId = id });
        using var missing = await host.Client.GetAsync("cc/advanced/" + id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ProxyPreservesPathsBasicRulesAndAudit()
    {
        await using var host = await Host.Start();
        await host.Post("statistic/white-paths/add", new { path = "/health" });
        await host.Post("statistic/path-stas/add", new { path = "/api" });
        var config = await host.Get("statistic/config");
        Assert.Equal("/health", config.GetProperty("whitePaths")[0].GetString());
        Assert.Equal("/api", config.GetProperty("pathStas")[0].GetString());
        await host.Post("statistic/white-paths/remove", new { path = "/health" });
        await host.Post("statistic/path-stas/remove", new { path = "/api" });
        await host.Post("cc/rules/add", new { path = "/api", period = 10, limitNum = 20, fbTime = "00:05:00" });
        Assert.Equal(1, (await host.Get("cc/rules")).GetProperty("total").GetInt32());
        await host.Post("cc/rules/remove", new { path = "/api" });
        Assert.Equal(0, (await host.Get("cc/rules")).GetProperty("total").GetInt32());
        Assert.Contains(host.Audit.Actions, action => action.Contains("添加白名单路径"));
        Assert.Contains(host.Audit.Actions, action => action.Contains("插件 API"));
    }

    [Theory]
    [InlineData("cc/advanced/add", "{\"name\":\"bad\",\"threshold\":-1}")]
    [InlineData("cc/advanced/add", "{\"name\":\"bad\",\"type\":\"invalid\"}")]
    [InlineData("cc/advanced/add", "{\"name\":\"bad\",\"conditionGroups\":[null]}")]
    [InlineData("cc/advanced/add", "{\"name\":\"bad\",\"conditions\":[{\"target\":\"invalid\"}]}")]
    [InlineData("cc/rules/add", "{\"path\":\"/\",\"period\":0}")]
    [InlineData("statistic/white-paths/add", "{}")]
    [InlineData("cc/advanced/add", "null")]
    [InlineData("cc/advanced/add", "{")]
    public async Task InvalidJsonOrFieldsCannotMutateState(string path, string json)
    {
        await using var host = await Host.Start();
        using var response = await host.Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(host.Statistic.GetAdvancedCcRules());
        Assert.Empty(host.Statistic.GetLimitCcRules());
        Assert.Empty(host.Statistic.GetWhitePaths());
    }

    [Fact]
    public async Task DisabledAndUnpermittedPluginsCannotReadOrWrite()
    {
        await using var host = await Host.Start();
        host.Manager.State = PluginState.Stopped;
        using var read = await host.Client.GetAsync("cc/advanced");
        using var write = await host.Client.PostAsJsonAsync("cc/advanced/add", new { name = "blocked" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);
        Assert.Empty(host.Statistic.GetAdvancedCcRules());
        host.Manager.State = PluginState.Running;
        host.Manager.Granted = false;
        using var denied = await host.Client.GetAsync("cc/advanced");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        host.Manager.Granted = true;
        Assert.True((await host.Get("cc/advanced")).GetProperty("success").GetBoolean());
        using var old = await host.Client.GetAsync("/api/cc/advanced");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        using var wrongMethod = await host.Client.PutAsJsonAsync("cc/advanced/add", new { name = "blocked" });
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);
    }

    private sealed class Host(WebApplication app, HttpClient client, Manager manager, Statistic statistic, Audit audit) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public Manager Manager => manager;
        public Statistic Statistic => statistic;
        public Audit Audit => audit;
        public static async Task<Host> Start()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var manager = new Manager();
            var statistic = new Statistic();
            var audit = new Audit();
            builder.Services.AddSingleton<IPluginManager>(manager);
            builder.Services.AddSingleton<IStatisticService>(statistic);
            builder.Services.AddSingleton<IAuditLogService>(audit);
            builder.Services.Configure<PluginOptions>(o => o.ApiProxy.RequestsPerMinute = 1000);
            builder.Services.AddSingleton<PluginApiAdmissionGate>();
            var app = builder.Build();
            app.MapLyWafPluginApi(12345);
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/api/plugins/analysis/proxy/") };
            client.DefaultRequestHeaders.Host = "localhost:12345";
            return new(app, client, manager, statistic, audit);
        }
        public async Task<JsonElement> Get(string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task<JsonElement> Post(string path, object body)
        {
            using var response = await client.PostAsJsonAsync(path, body);
            var json = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, json);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class Manager : IPluginManager
    {
        private readonly AnalysisPlugin _plugin = new();
        public PluginState State = PluginState.Running;
        public bool Granted = true;
        public ILyWafPlugin? GetPlugin(string id) => id == "analysis" ? _plugin : null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [_plugin];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [_plugin];
        public bool IsSystemPlugin(string id) => id == "analysis";
        public PluginState GetPluginState(string id) => State;
        public bool IsPermissionGranted(string id, PluginPermission permission) => Granted;
        public Task EnablePluginAsync(string id) { State = PluginState.Running; return Task.CompletedTask; }
        public Task DisablePluginAsync(string id) { State = PluginState.Stopped; return Task.CompletedTask; }
        public Task ReloadPluginAsync(string id) => Task.CompletedTask;
    }

    private sealed class Audit : IAuditLogService
    {
        public List<string> Actions { get; } = [];
        public void Log(string username, string action, string ip) => Actions.Add(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }

    // Keep API tests isolated from the real statistic service's persistent state file.
    private sealed class Statistic : IStatisticService
    {
        private readonly List<AdvancedCcRule> _rules = [];
        private readonly List<LimitCcOption> _basic = [];
        private readonly List<string> _white = [], _paths = [];
        public Task<string> GetMatchPath(string path) => Task.FromResult(path);
        public bool IsWhitePath(string path) => _white.Contains(path);
        public StatisticOptions GetOption() => new();
        public List<LimitCcOption> GetLimitCcRules() => _basic.ToList();
        public bool AddLimitCcRule(LimitCcOption rule) { _basic.Add(rule); return true; }
        public bool RemoveLimitCcRule(string path) => _basic.RemoveAll(r => r.Path == path) > 0;
        public List<AdvancedCcRule> GetAdvancedCcRules() => _rules.ToList();
        public List<AdvancedCcRule> GetAdvancedCcRulesByType(CcRuleType type) => _rules.Where(r => r.Type == type).ToList();
        public AdvancedCcRule? GetAdvancedCcRule(string id) => _rules.FirstOrDefault(r => r.Id == id);
        public bool AddAdvancedCcRule(AdvancedCcRule rule) { _rules.Add(rule); return true; }
        public bool UpdateAdvancedCcRule(AdvancedCcRule rule) { var i = _rules.FindIndex(r => r.Id == rule.Id); if (i < 0) return false; _rules[i] = rule; return true; }
        public bool RemoveAdvancedCcRule(string id) => _rules.RemoveAll(r => r.Id == id) > 0;
        public bool ToggleAdvancedCcRule(string id, bool enabled) { var r = GetAdvancedCcRule(id); if (r == null) return false; r.Enabled = enabled; return true; }
        public List<string> GetWhitePaths() => _white.ToList();
        public bool AddWhitePath(string path) { _white.Add(path); return true; }
        public bool RemoveWhitePath(string path) => _white.Remove(path);
        public List<string> GetPathStas() => _paths.ToList();
        public bool AddPathSta(string path) { _paths.Add(path); return true; }
        public bool RemovePathSta(string path) => _paths.Remove(path);
    }
}
