namespace LyWaf.Services.WafRule;

/// <summary>
/// 系统内置 WAF 规则（预置常见攻击防护）
/// </summary>
public static class SystemWafRules
{
    /// <summary>
    /// 获取所有系统内置规则
    /// </summary>
    public static List<WafCustomRule> GetRules()
    {
        return
        [
            // ────────────────── 扫描器 UA 检测 ──────────────────
            new WafCustomRule
            {
                Id = "sys-scanner-ua",
                Name = "扫描器 UA 检测",
                Description = "检测常见安全扫描器和恶意爬虫的 User-Agent 特征",
                Enabled = true,
                Priority = 40,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                Conditions =
                [
                    new WafCondition
                    {
                        Field = WafMatchField.UserAgent,
                        Operator = WafMatchOperator.Regex,
                        Value = @"(?i)(sqlmap|nikto|nessus|acunetix|netsparker|w3af|openvas|masscan|zgrab|nuclei|dirsearch|gobuster|ffuf|wfuzz|hydra|medusa|nmap\s+scripting|havij|webscarab|commix)",
                        IgnoreCase = true,
                    }
                ]
            },

            // ────────────────── 敏感文件访问防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-sensitive-files",
                Name = "敏感文件访问防护",
                Description = "阻止访问配置文件、版本控制目录、备份文件等敏感路径",
                Enabled = true,
                Priority = 50,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                Conditions =
                [
                    new WafCondition
                    {
                        Field = WafMatchField.UriPath,
                        Operator = WafMatchOperator.Regex,
                        Value = @"(?i)(/\.git/|/\.svn/|/\.hg/|/\.env|/\.htaccess|/\.htpasswd|/\.DS_Store|/web\.config|/wp-config\.php|/\.aws/|/\.ssh/|\.(bak|swp|old|orig|save|dist|config|ini|log|sql|tar|gz|zip|rar|7z|dump)$)",
                        IgnoreCase = true,
                    }
                ]
            },

        ];
    }
}
