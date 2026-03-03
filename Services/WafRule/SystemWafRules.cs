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
            // ────────────────── SQL 注入防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-sqli",
                Name = "SQL 注入防护",
                Description = "检测常见 SQL 注入攻击模式（union select、insert into、drop table 等）",
                Enabled = true,
                Priority = 10,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                ConditionGroups =
                [
                    // 检测查询字符串中的 SQL 注入
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.QueryString,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(union\s+(all\s+)?select|select\s+.+from\s+|insert\s+into|delete\s+from|drop\s+(table|database)|update\s+.+set\s+|'\s*(or|and)\s*'|--\s|;\s*(drop|delete|update|insert))",
                                IgnoreCase = true,
                            }
                        ]
                    },
                    // 检测 URL 路径中的 SQL 注入
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.UriPath,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(union\s+(all\s+)?select|select\s+.+from\s+|insert\s+into|delete\s+from|drop\s+(table|database)|update\s+.+set\s+)",
                                IgnoreCase = true,
                            }
                        ]
                    },
                    // 检测请求体中的 SQL 注入
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.Body,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(union\s+(all\s+)?select|select\s+.+from\s+|insert\s+into|delete\s+from|drop\s+(table|database)|update\s+.+set\s+|'\s*(or|and)\s*')",
                                IgnoreCase = true,
                            }
                        ]
                    },
                ]
            },

            // ────────────────── XSS 攻击防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-xss",
                Name = "XSS 攻击防护",
                Description = "检测跨站脚本攻击模式（script 标签、javascript 协议、事件处理器等）",
                Enabled = true,
                Priority = 20,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Reject,
                ResponseCode = 403,
                ConditionGroups =
                [
                    // 检测查询字符串中的 XSS
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.QueryString,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(<script[\s>]|javascript\s*:|on(error|load|click|mouseover|focus|blur|submit|change|input)\s*=|<\s*img[^>]+src\s*=\s*[""']?javascript|<\s*iframe|<\s*object|<\s*embed|expression\s*\(|vbscript\s*:)",
                                IgnoreCase = true,
                            }
                        ]
                    },
                    // 检测请求体中的 XSS
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.Body,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(<script[\s>]|javascript\s*:|on(error|load|click|mouseover|focus|blur|submit|change|input)\s*=|<\s*img[^>]+src\s*=\s*[""']?javascript|<\s*iframe|<\s*object|<\s*embed)",
                                IgnoreCase = true,
                            }
                        ]
                    },
                ]
            },

            // ────────────────── 路径遍历防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-path-traversal",
                Name = "路径遍历防护",
                Description = "检测目录穿越攻击模式（../、%2e%2e 等编码变体）",
                Enabled = true,
                Priority = 30,
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
                                Value = @"(\.\./|\.\.\\|%2e%2e%2f|%2e%2e/|\.%2e/|%2e\.\.|\.\.%5c|%252e%252e)",
                                IgnoreCase = true,
                            }
                        ]
                    },
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
                    },
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
                    // 版本控制和敏感目录
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.UriPath,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(/\.git/|/\.svn/|/\.hg/|/\.env|/\.htaccess|/\.htpasswd|/\.DS_Store|/web\.config|/wp-config\.php|/\.aws/|/\.ssh/)",
                                IgnoreCase = true,
                            }
                        ]
                    },
                    // 备份文件和敏感扩展名
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.UriPath,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)\.(bak|swp|old|orig|save|dist|config|ini|log|sql|tar|gz|zip|rar|7z|dump)$",
                                IgnoreCase = true,
                            }
                        ]
                    },
                ]
            },

            // ────────────────── Shell 命令注入防护 ──────────────────
            new WafCustomRule
            {
                Id = "sys-cmd-injection",
                Name = "命令注入防护",
                Description = "检测常见操作系统命令注入模式",
                Enabled = true,
                Priority = 60,
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
                                Field = WafMatchField.QueryString,
                                Operator = WafMatchOperator.Regex,
                                Value = @"(?i)(\|\s*\w|;\s*(ls|cat|whoami|id|pwd|uname|wget|curl|bash|sh|cmd|powershell)|`[^`]+`|\$\(.*\)|\b(eval|exec|system|passthru|popen)\s*\()",
                                IgnoreCase = true,
                            }
                        ]
                    },
                ]
            },

            // ────────────────── HTTP 协议异常检测 ──────────────────
            new WafCustomRule
            {
                Id = "sys-protocol-anomaly",
                Name = "HTTP 协议异常检测",
                Description = "检测异常 HTTP 请求（缺失 User-Agent、异常方法等）",
                Enabled = false, // 默认关闭，可能影响 API 调用
                Priority = 70,
                Source = WafRuleSource.System,
                Action = WafRuleAction.Observe,
                ResponseCode = 403,
                ConditionGroups =
                [
                    // 缺失 User-Agent 的请求
                    new WafConditionGroup
                    {
                        Conditions =
                        [
                            new WafCondition
                            {
                                Field = WafMatchField.UserAgent,
                                Operator = WafMatchOperator.NotExists,
                                Value = "",
                                IgnoreCase = true,
                            }
                        ]
                    },
                ]
            },
        ];
    }
}
