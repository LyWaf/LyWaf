using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Protect;
using LyWaf.Services.Statistic;
using LyWaf.Shared;

namespace LyWaf.Control;

/// <summary>
/// 控制面板 HTML 模板生成器
/// </summary>
public static class ControlTemplate
{
    // 缓存 HTML 模板
    private static string? _dashboardTemplate;
    private static DateTime _templateLastModified;
    
    /// <summary>
    /// 获取 HTML 模板
    /// </summary>
    public static string GetDashboardTemplate()
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html");
        if (!File.Exists(templatePath))
        {
            templatePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "dashboard.html");
        }
        
        if (!File.Exists(templatePath))
        {
            return "<html><body><h1>模板文件未找到</h1></body></html>";
        }
        
        var lastModified = File.GetLastWriteTime(templatePath);
        if (_dashboardTemplate == null || lastModified > _templateLastModified)
        {
            _dashboardTemplate = File.ReadAllText(templatePath, Encoding.UTF8);
            _templateLastModified = lastModified;
        }
        
        return _dashboardTemplate;
    }
    
    /// <summary>
    /// 获取最近 N 分钟内访问的客户端 IP
    /// </summary>
    public static List<(string Ip, DateTime LastAccess)> GetRecentClients(int minutes = 5)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var threshold = now - (minutes * 60 * 1000);
        
        var clientStas = SharedData.ClientStas.GetSnapshot();
        var recentClients = clientStas
            .Where(kv => kv.Value.LastAccessTime >= threshold)
            .Select(kv => (
                Ip: kv.Key, 
                LastAccess: DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastAccessTime).LocalDateTime
            ))
            .OrderByDescending(x => x.LastAccess)
            .ToList();
        
        return recentClients;
    }
    
    /// <summary>
    /// 生成最近客户端 HTML
    /// </summary>
    public static string GenerateRecentClientsHtml(List<(string Ip, DateTime LastAccess)> recentClients)
    {
        if (recentClients.Count == 0)
        {
            return "<div class=\"empty-state\">最近 5 分钟内暂无访问记录</div>";
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"recent-ips-container\">");
        
        foreach (var (Ip, LastAccess) in recentClients.Take(50))
        {
            var timeAgo = DateTime.Now - LastAccess;
            var timeAgoStr = timeAgo.TotalSeconds < 60 
                ? $"{(int)timeAgo.TotalSeconds} 秒前"
                : timeAgo.TotalMinutes < 60 
                    ? $"{(int)timeAgo.TotalMinutes} 分钟前"
                    : $"{LastAccess:HH:mm:ss}";
            
            sb.AppendLine($"    <div class=\"recent-ip-item\">");
            sb.AppendLine($"        <span class=\"ip\">{Ip}</span>");
            sb.AppendLine($"        <span class=\"time\">{timeAgoStr}</span>");
            sb.AppendLine($"    </div>");
        }
        
        if (recentClients.Count > 50)
        {
            sb.AppendLine($"    <div class=\"empty-state\">... 还有 {recentClients.Count - 50} 个</div>");
        }
        
        sb.AppendLine("</div>");
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成 CC 规则 HTML
    /// </summary>
    public static string GenerateCcRulesHtml(List<LimitCcOption> ccRules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"list-actions\">");
        sb.AppendLine("    <button class=\"btn btn-primary btn-sm\" onclick=\"addCcRule()\">+ 添加规则</button>");
        sb.AppendLine("</div>");
        
        if (ccRules.Count == 0)
        {
            sb.AppendLine("<div class=\"empty-state\">暂无 CC 防护规则</div>");
            return sb.ToString();
        }
        
        sb.AppendLine("<table>");
        sb.AppendLine("    <tr>");
        sb.AppendLine("        <th>路径</th>");
        sb.AppendLine("        <th>限制</th>");
        sb.AppendLine("        <th>时间窗口</th>");
        sb.AppendLine("        <th>封禁时长</th>");
        sb.AppendLine("        <th>操作</th>");
        sb.AppendLine("    </tr>");
        
        foreach (var r in ccRules)
        {
            var path = string.IsNullOrEmpty(r.Path) ? "/" : r.Path;
            var displayPath = string.IsNullOrEmpty(r.Path) ? "全局" : r.Path;
            sb.AppendLine($"    <tr>");
            sb.AppendLine($"        <td><code>{displayPath}</code></td>");
            sb.AppendLine($"        <td>{r.LimitNum} 次</td>");
            sb.AppendLine($"        <td>{r.Period} 秒</td>");
            sb.AppendLine($"        <td>{r.FbTime.TotalSeconds} 秒</td>");
            sb.AppendLine($"        <td><button class=\"btn-icon btn-delete\" onclick=\"removeCcRule('{path}')\">×</button></td>");
            sb.AppendLine($"    </tr>");
        }
        
        sb.AppendLine("</table>");
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成封禁 IP HTML
    /// </summary>
    public static string GenerateBlockedIpsHtml(List<KeyValuePair<string, string>> blockedIpList)
    {
        if (blockedIpList.Count == 0)
        {
            return "<div class=\"empty-state\">暂无封禁的 IP</div>";
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"item-list\">");
        
        foreach (var item in blockedIpList.Take(50))
        {
            var ip = item.Key;
            var reason = System.Web.HttpUtility.HtmlEncode(item.Value);
            sb.AppendLine($"    <div class=\"item-row blocked-ip-row\">");
            sb.AppendLine($"        <span class=\"item-text\">{ip}</span>");
            sb.AppendLine($"        <span class=\"item-reason\">{reason}</span>");
            sb.AppendLine($"        <button class=\"btn-icon btn-delete\" onclick=\"unblockIp('{ip}')\">×</button>");
            sb.AppendLine($"    </div>");
        }
        
        if (blockedIpList.Count > 50)
        {
            sb.AppendLine($"    <div class=\"item-more\">... 还有 {blockedIpList.Count - 50} 个</div>");
        }
        
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成 IP 白名单 HTML
    /// </summary>
    public static string GenerateWhitelistHtml(List<string> whitelist)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"list-actions\">");
        sb.AppendLine("    <button class=\"btn btn-primary btn-sm\" onclick=\"addWhitelistIp()\">+ 添加</button>");
        sb.AppendLine("</div>");
        
        if (whitelist.Count == 0)
        {
            sb.AppendLine("<div class=\"empty-state\">暂无白名单规则</div>");
            return sb.ToString();
        }
        
        sb.AppendLine("<div class=\"item-list\">");
        foreach (var ip in whitelist.Take(20))
        {
            sb.AppendLine($"    <div class=\"item-row\">");
            sb.AppendLine($"        <span class=\"item-text\">{ip}</span>");
            sb.AppendLine($"        <button class=\"btn-icon btn-delete\" onclick=\"removeWhitelistIp('{ip}')\">×</button>");
            sb.AppendLine($"    </div>");
        }
        if (whitelist.Count > 20)
        {
            sb.AppendLine($"    <div class=\"item-more\">... 还有 {whitelist.Count - 20} 条</div>");
        }
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成 IP 黑名单 HTML
    /// </summary>
    public static string GenerateBlacklistHtml(List<string> blacklist)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"list-actions\">");
        sb.AppendLine("    <button class=\"btn btn-primary btn-sm\" onclick=\"addBlacklistIp()\">+ 添加</button>");
        sb.AppendLine("</div>");
        
        if (blacklist.Count == 0)
        {
            sb.AppendLine("<div class=\"empty-state\">暂无黑名单规则</div>");
            return sb.ToString();
        }
        
        sb.AppendLine("<div class=\"item-list\">");
        foreach (var ip in blacklist.Take(20))
        {
            sb.AppendLine($"    <div class=\"item-row\">");
            sb.AppendLine($"        <span class=\"item-text\">{ip}</span>");
            sb.AppendLine($"        <button class=\"btn-icon btn-delete\" onclick=\"removeBlacklistIp('{ip}')\">×</button>");
            sb.AppendLine($"    </div>");
        }
        if (blacklist.Count > 20)
        {
            sb.AppendLine($"    <div class=\"item-more\">... 还有 {blacklist.Count - 20} 条</div>");
        }
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成地理位置访问控制 HTML
    /// </summary>
    public static string GenerateGeoAccessHtml(
        List<string> allowCountries, List<string> allowRegions,
        List<string> denyCountries, List<string> denyRegions)
    {
        var sb = new StringBuilder();
        
        // 允许国家
        sb.AppendLine("<div class=\"geo-section\">");
        sb.AppendLine("    <div class=\"geo-header\">");
        sb.AppendLine("        <span class=\"tag tag-green\">允许国家</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addAllowCountry()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (allowCountries.Count > 0)
        {
            sb.AppendLine("    <div class=\"geo-items\">");
            foreach (var c in allowCountries)
            {
                sb.AppendLine($"        <span class=\"geo-item\">{c} <button class=\"btn-x\" onclick=\"removeAllowCountry('{c}')\">×</button></span>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"geo-empty\">未设置</div>");
        }
        sb.AppendLine("</div>");
        
        // 允许省份
        sb.AppendLine("<div class=\"geo-section\">");
        sb.AppendLine("    <div class=\"geo-header\">");
        sb.AppendLine("        <span class=\"tag tag-green\">允许省份</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addAllowRegion()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (allowRegions.Count > 0)
        {
            sb.AppendLine("    <div class=\"geo-items\">");
            foreach (var r in allowRegions)
            {
                sb.AppendLine($"        <span class=\"geo-item\">{r} <button class=\"btn-x\" onclick=\"removeAllowRegion('{r}')\">×</button></span>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"geo-empty\">未设置</div>");
        }
        sb.AppendLine("</div>");
        
        // 禁止国家
        sb.AppendLine("<div class=\"geo-section\">");
        sb.AppendLine("    <div class=\"geo-header\">");
        sb.AppendLine("        <span class=\"tag tag-red\">禁止国家</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addDenyCountry()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (denyCountries.Count > 0)
        {
            sb.AppendLine("    <div class=\"geo-items\">");
            foreach (var c in denyCountries)
            {
                sb.AppendLine($"        <span class=\"geo-item\">{c} <button class=\"btn-x\" onclick=\"removeDenyCountry('{c}')\">×</button></span>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"geo-empty\">未设置</div>");
        }
        sb.AppendLine("</div>");
        
        // 禁止省份
        sb.AppendLine("<div class=\"geo-section\">");
        sb.AppendLine("    <div class=\"geo-header\">");
        sb.AppendLine("        <span class=\"tag tag-red\">禁止省份</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addDenyRegion()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (denyRegions.Count > 0)
        {
            sb.AppendLine("    <div class=\"geo-items\">");
            foreach (var r in denyRegions)
            {
                sb.AppendLine($"        <span class=\"geo-item\">{r} <button class=\"btn-x\" onclick=\"removeDenyRegion('{r}')\">×</button></span>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"geo-empty\">未设置</div>");
        }
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成 WAF 规则 HTML
    /// </summary>
    public static string GenerateWafRulesHtml(List<string> argsRules, List<string> postRules)
    {
        var sb = new StringBuilder();
        
        // Args 规则
        sb.AppendLine("<div class=\"waf-section\">");
        sb.AppendLine("    <div class=\"waf-header\">");
        sb.AppendLine("        <span class=\"waf-title\">参数检测规则 (Args)</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addArgsRule()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (argsRules.Count > 0)
        {
            sb.AppendLine("    <div class=\"waf-rules\">");
            foreach (var rule in argsRules.Take(10))
            {
                var displayRule = rule.Length > 60 ? rule[..60] + "..." : rule;
                var escapedRule = System.Web.HttpUtility.HtmlEncode(rule).Replace("'", "\\'");
                sb.AppendLine($"        <div class=\"waf-rule\">");
                sb.AppendLine($"            <code title=\"{System.Web.HttpUtility.HtmlEncode(rule)}\">{System.Web.HttpUtility.HtmlEncode(displayRule)}</code>");
                sb.AppendLine($"            <button class=\"btn-x\" onclick=\"removeArgsRule('{escapedRule}')\">×</button>");
                sb.AppendLine($"        </div>");
            }
            if (argsRules.Count > 10)
            {
                sb.AppendLine($"        <div class=\"waf-more\">... 还有 {argsRules.Count - 10} 条规则</div>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"empty-state-sm\">暂无规则</div>");
        }
        sb.AppendLine("</div>");
        
        // Post 规则
        sb.AppendLine("<div class=\"waf-section\">");
        sb.AppendLine("    <div class=\"waf-header\">");
        sb.AppendLine("        <span class=\"waf-title\">POST 检测规则</span>");
        sb.AppendLine("        <button class=\"btn btn-primary btn-sm\" onclick=\"addPostRule()\">+ 添加</button>");
        sb.AppendLine("    </div>");
        if (postRules.Count > 0)
        {
            sb.AppendLine("    <div class=\"waf-rules\">");
            foreach (var rule in postRules.Take(10))
            {
                var displayRule = rule.Length > 60 ? rule[..60] + "..." : rule;
                var escapedRule = System.Web.HttpUtility.HtmlEncode(rule).Replace("'", "\\'");
                sb.AppendLine($"        <div class=\"waf-rule\">");
                sb.AppendLine($"            <code title=\"{System.Web.HttpUtility.HtmlEncode(rule)}\">{System.Web.HttpUtility.HtmlEncode(displayRule)}</code>");
                sb.AppendLine($"            <button class=\"btn-x\" onclick=\"removePostRule('{escapedRule}')\">×</button>");
                sb.AppendLine($"        </div>");
            }
            if (postRules.Count > 10)
            {
                sb.AppendLine($"        <div class=\"waf-more\">... 还有 {postRules.Count - 10} 条规则</div>");
            }
            sb.AppendLine("    </div>");
        }
        else
        {
            sb.AppendLine("    <div class=\"empty-state-sm\">暂无规则</div>");
        }
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成 A/B 测试 HTML
    /// </summary>
    public static string GenerateAbTestHtml(Dictionary<string, ABTestConfig> abTests)
    {
        if (abTests.Count == 0)
        {
            return "<div class=\"empty-state\">暂无 A/B 测试配置</div>";
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"table-container\">");
        sb.AppendLine("    <table>");
        sb.AppendLine("        <tr>");
        sb.AppendLine("            <th>测试 ID</th>");
        sb.AppendLine("            <th>名称</th>");
        sb.AppendLine("            <th>状态</th>");
        sb.AppendLine("            <th>模式</th>");
        sb.AppendLine("            <th>变体</th>");
        sb.AppendLine("        </tr>");
        
        foreach (var kv in abTests)
        {
            var statusClass = kv.Value.Enabled ? "tag-green" : "tag-red";
            var statusText = kv.Value.Enabled ? "启用" : "禁用";
            var variants = string.Join(", ", kv.Value.Variants.Select(v => $"{v.Key}:{v.Value}%"));
            
            sb.AppendLine($"        <tr>");
            sb.AppendLine($"            <td><code>{kv.Key}</code></td>");
            sb.AppendLine($"            <td>{kv.Value.Name}</td>");
            sb.AppendLine($"            <td><span class=\"tag {statusClass}\">{statusText}</span></td>");
            sb.AppendLine($"            <td>{kv.Value.Mode}</td>");
            sb.AppendLine($"            <td>{variants}</td>");
            sb.AppendLine($"        </tr>");
        }
        
        sb.AppendLine("    </table>");
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成功能状态 HTML
    /// </summary>
    public static string GenerateFeatureStatusHtml(
        bool ipControlEnabled, bool geoControlEnabled, 
        bool wafArgsEnabled, bool wafPostEnabled, bool ccEnabled)
    {
        var sb = new StringBuilder();
        
        void AddStatus(string name, string featureId, bool enabled, bool canToggle = true)
        {
            var statusClass = enabled ? "status-on" : "status-off";
            var statusText = enabled ? "启用" : "禁用";
            var icon = enabled ? "✓" : "✗";
            var clickable = canToggle ? "clickable" : "";
            var onclick = canToggle ? $"onclick=\"toggleFeature('{featureId}')\"" : "";
            var title = canToggle ? "title=\"点击切换状态\"" : "";
            
            sb.AppendLine($"    <div class=\"feature-item {statusClass} {clickable}\" data-feature=\"{featureId}\" {onclick} {title}>");
            sb.AppendLine($"        <span class=\"feature-icon\">{icon}</span>");
            sb.AppendLine($"        <span class=\"feature-name\">{name}</span>");
            sb.AppendLine($"        <span class=\"feature-status\">{statusText}</span>");
            sb.AppendLine($"    </div>");
        }
        
        sb.AppendLine("<div class=\"feature-grid\">");
        AddStatus("IP 访问控制", "ip-control", ipControlEnabled);
        AddStatus("地理位置控制", "geo-control", geoControlEnabled);
        AddStatus("WAF Args检测", "waf-args", wafArgsEnabled);
        AddStatus("WAF Post检测", "waf-post", wafPostEnabled);
        AddStatus("CC 防护", "cc", true, false); // CC 防护无法直接切换，需要添加/删除规则
        sb.AppendLine("</div>");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 生成概览页面 HTML
    /// </summary>
    public static string GenerateDashboardHtml(
        System.Diagnostics.Process process,
        TimeSpan uptime,
        ConnectionStats connectionStats,
        Struct.ExpiringSafeDictionary<string, string> blockedIps,
        IAccessControlService accessControlService,
        IStatisticService statisticService,
        IProtectService protectService,
        Dictionary<string, ABTestConfig> abTests)
    {
        // 获取配置选项
        var acOptions = accessControlService.GetOptions();
        var protectOptions = protectService.GetOptions();
        var statisticOptions = statisticService.GetOption();
        
        // 获取功能启用状态
        var ipControlEnabled = acOptions.IpControl.Enabled;
        var geoControlEnabled = acOptions.GeoControl.Enabled;
        var wafArgsEnabled = protectOptions.OpenArgsCheck;
        var wafPostEnabled = protectOptions.OpenPostCheck;
        var ccEnabled = statisticOptions.LimitCc.Count > 0 || statisticService.GetLimitCcRules().Count > 0;
        
        // 获取各项数据
        var whitelist = accessControlService.GetWhitelist();
        var blacklist = accessControlService.GetBlacklist();
        var denyCountries = accessControlService.GetDenyCountries();
        var denyRegions = accessControlService.GetDenyRegions();
        var allowCountries = accessControlService.GetAllowCountries();
        var allowRegions = accessControlService.GetAllowRegions();
        var ccRules = statisticService.GetLimitCcRules();
        var argsRules = protectService.GetArgsRegexList();
        var postRules = protectService.GetPostRegexList();
        var blockedIpList = blockedIps.GetValidItems().ToList();
        var recentClients = GetRecentClients(5);

        // 格式化运行时间
        var uptimeStr = uptime.Days > 0 
            ? $"{uptime.Days}天 {uptime.Hours}小时 {uptime.Minutes}分钟" 
            : uptime.Hours > 0 
                ? $"{uptime.Hours}小时 {uptime.Minutes}分钟 {uptime.Seconds}秒"
                : $"{uptime.Minutes}分钟 {uptime.Seconds}秒";

        // 读取模板并替换占位符
        var template = GetDashboardTemplate();
        
        var html = template
            .Replace("{{UPTIME_STR}}", uptimeStr)
            .Replace("{{PROCESS_START_TIME}}", process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{{MEMORY_MB}}", (process.WorkingSet64 / (1024 * 1024)).ToString())
            .Replace("{{TOTAL_CONNECTIONS}}", connectionStats.TotalConnections.ToString())
            .Replace("{{UNIQUE_IPS}}", connectionStats.ConnectionsPerIp.Count.ToString())
            .Replace("{{BLOCKED_IP_COUNT}}", blockedIpList.Count.ToString())
            .Replace("{{FEATURE_STATUS_CONTENT}}", GenerateFeatureStatusHtml(ipControlEnabled, geoControlEnabled, wafArgsEnabled, wafPostEnabled, ccEnabled))
            .Replace("{{RECENT_CLIENTS_COUNT}}", recentClients.Count.ToString())
            .Replace("{{RECENT_CLIENTS_CONTENT}}", GenerateRecentClientsHtml(recentClients))
            .Replace("{{WHITELIST_COUNT}}", whitelist.Count.ToString())
            .Replace("{{BLACKLIST_COUNT}}", blacklist.Count.ToString())
            .Replace("{{WHITELIST_CONTENT}}", GenerateWhitelistHtml(whitelist))
            .Replace("{{BLACKLIST_CONTENT}}", GenerateBlacklistHtml(blacklist))
            .Replace("{{ALLOW_COUNTRIES_COUNT}}", allowCountries.Count.ToString())
            .Replace("{{ALLOW_REGIONS_COUNT}}", allowRegions.Count.ToString())
            .Replace("{{DENY_COUNTRIES_COUNT}}", denyCountries.Count.ToString())
            .Replace("{{DENY_REGIONS_COUNT}}", denyRegions.Count.ToString())
            .Replace("{{GEO_ACCESS_CONTENT}}", GenerateGeoAccessHtml(allowCountries, allowRegions, denyCountries, denyRegions))
            .Replace("{{ARGS_RULES_COUNT}}", argsRules.Count.ToString())
            .Replace("{{POST_RULES_COUNT}}", postRules.Count.ToString())
            .Replace("{{WAF_RULES_CONTENT}}", GenerateWafRulesHtml(argsRules, postRules))
            .Replace("{{CC_RULES_CONTENT}}", GenerateCcRulesHtml(ccRules))
            .Replace("{{BLOCKED_IPS_CONTENT}}", GenerateBlockedIpsHtml(blockedIpList))
            .Replace("{{ABTEST_COUNT}}", abTests.Count.ToString())
            .Replace("{{ABTEST_CONTENT}}", GenerateAbTestHtml(abTests))
            .Replace("{{REFRESH_TIME}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        return html;
    }
}
