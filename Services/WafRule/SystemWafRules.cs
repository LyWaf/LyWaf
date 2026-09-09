namespace LyWaf.Services.WafRule;

/// <summary>
/// 系统内置 WAF 规则（预置常见攻击防护）
/// </summary>
public static class SystemWafRules
{
    private const string InjectionPattern = @"(?:\bunion(?:\s+all)?\s+select\b|\bselect\b.{0,256}\bfrom\b|['""]\s*(?:or|and)\s+\d+\s*=\s*\d+|\b(?:sleep|benchmark|pg_sleep)\s*\(|\binto\s+(?:dump|out)file\b|<\s*(?:script|iframe)\b|\bon(?:error|load|mouseover)\s*=|(?:;|&&|\|\|)\s*(?:sh|bash|cmd|powershell|curl|wget)\b|\b(?:eval|system|exec|passthru|shell_exec)\s*\()";

    /// <summary>
    /// 获取所有系统内置规则
    /// </summary>
    public static List<WafCustomRule> GetRules()
    {
        return
        [
            // ────────────────── GET 参数注入防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-get-injection",
                Name = "GET 参数注入防护",
                Description = "检测 GET 查询参数中的 SQL、XSS、命令和代码注入特征",
                Enabled = true,
                Priority = 10,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                GroupOperator = WafLogicalOperator.Or,
                ConditionGroups =
                [
                    new WafConditionGroup
                    {
                        Operator = WafLogicalOperator.And,
                        Conditions =
                        [
                            new WafCondition { Field = WafMatchField.Method, Operator = WafMatchOperator.Equal, Value = "GET" },
                            new WafCondition { Field = WafMatchField.QueryString, Operator = WafMatchOperator.Regex, Value = InjectionPattern }
                        ]
                    }
                ]
            },

            // ────────────────── POST 请求体注入防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-post-injection",
                Name = "POST Body 注入防护",
                Description = "检测 POST 请求体中的 SQL、XSS、命令和代码注入特征",
                Enabled = true,
                Priority = 20,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                GroupOperator = WafLogicalOperator.Or,
                ConditionGroups =
                [
                    new WafConditionGroup
                    {
                        Operator = WafLogicalOperator.And,
                        Conditions =
                        [
                            new WafCondition { Field = WafMatchField.Method, Operator = WafMatchOperator.Equal, Value = "POST" },
                            new WafCondition { Field = WafMatchField.Body, Operator = WafMatchOperator.Regex, Value = InjectionPattern }
                        ]
                    }
                ]
            },

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
                ConditionGroups =
                [
                    new WafConditionGroup
                    {
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
                ConditionGroups =
                [
                    new WafConditionGroup
                    {
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
                    }
                ]
            },

        ];
    }
}
