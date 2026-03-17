using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Captcha;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using LyWaf.Services.Auth;
using LyWaf.Services.WafRule;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Acme;
using LyWaf.Shared;
using System.Security.Cryptography.X509Certificates;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LyWaf.Control;

public static class ControlApi
{
    private static readonly HashSet<string> AllowedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileProvider", "Logging", "Protect", "ReverseProxy", 
        "SpeedLimit", "Statistic", "WafInfos"
    };
    
    // 缓存前端静态文件
    private static string? _indexHtmlCache;
    private static DateTime _indexHtmlLastModified;
    
    /// <summary>
    /// 获取前端静态文件目录
    /// </summary>
    private static string GetFrontendDistPath()
    {
        // 首先尝试 control_html 目录（构建后输出目录）
        var controlHtmlPath = Path.Combine(Directory.GetCurrentDirectory(), "control_html");
        if (Directory.Exists(controlHtmlPath)) return controlHtmlPath;
        
        // 然后尝试 Frontend/dist 目录（开发环境）
        var devPath = Path.Combine(Directory.GetCurrentDirectory(), "Frontend", "dist");
        if (Directory.Exists(devPath)) return devPath;
        
        // 然后尝试 BaseDirectory 下的 control_html（发布环境）
        var releasePath = Path.Combine(AppContext.BaseDirectory, "control_html");
        if (Directory.Exists(releasePath)) return releasePath;
        
        // 最后返回 BaseDirectory 下的 frontend
        return Path.Combine(AppContext.BaseDirectory, "frontend");
    }
    
    /// <summary>
    /// 获取 index.html 内容
    /// </summary>
    private static string GetIndexHtml()
    {
        var distPath = GetFrontendDistPath();
        var indexPath = Path.Combine(distPath, "index.html");
        
        if (!File.Exists(indexPath))
        {
            return @"<!DOCTYPE html>
<html><head><meta charset=""UTF-8""><title>LyWaf 控制台</title></head>
<body><h1>前端文件未找到</h1><p>请先构建前端: cd Frontend && npm run build</p></body></html>";
        }
        
        var lastModified = File.GetLastWriteTime(indexPath);
        if (_indexHtmlCache == null || lastModified > _indexHtmlLastModified)
        {
            _indexHtmlCache = File.ReadAllText(indexPath, Encoding.UTF8);
            _indexHtmlLastModified = lastModified;
        }
        
        return _indexHtmlCache;
    }
    
    /// <summary>
    /// 注册控制台 API 路由
    /// </summary>
    public static WebApplication MapControlApi(this WebApplication app, WafInfoOptions wafInfos)
    {
        var controlListen = wafInfos.GetControlListen();
        var controlPort = controlListen.Port;
        
        // =============== 前端静态文件服务 ===============
        
        var distPath = GetFrontendDistPath();
        if (Directory.Exists(distPath))
        {
            // 静态资源文件服务（JS/CSS/图片等）
            var fileProvider = new PhysicalFileProvider(distPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "",
                ServeUnknownFileTypes = false
            });
        }
        
        // SPA 入口 - 所有非 API 请求返回 index.html
        app.MapGet("/", (HttpContext ctx) =>
        {
            return Results.Content(GetIndexHtml(), "text/html; charset=utf-8");
        }).RequireHost($"*:{controlPort}");
        
        // SPA 子路由支持（security, api-timing 等）
        app.MapGet("/{*path}", (HttpContext ctx, string? path) =>
        {
            // 如果是 API 跳过
            if (path != null && path.StartsWith("api/"))
            {
                return Results.NotFound();
            }
            
            // 检查是否是静态文件
            if (path != null)
            {
                var distDir = GetFrontendDistPath();
                var filePath = Path.Combine(distDir, path);
                if (File.Exists(filePath))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    var contentType = ext switch
                    {
                        ".js" => "application/javascript",
                        ".css" => "text/css",
                        ".svg" => "image/svg+xml",
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".ico" => "image/x-icon",
                        ".woff" or ".woff2" => "font/woff2",
                        _ => "application/octet-stream"
                    };
                    return Results.File(filePath, contentType);
                }
            }
            
            // 返回 SPA index.html
            return Results.Content(GetIndexHtml(), "text/html; charset=utf-8");
        }).RequireHost($"*:{controlPort}");

        // =============== 认证端点（免认证） ===============

        app.MapPost("/api/auth/login", async (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var request = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            if (request == null || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.PasswordHash))
            {
                return Results.Json(new { success = false, message = "用户名和密码不能为空" }, statusCode: 400);
            }
            var result = authService.Login(request.Username, request.PasswordHash, request.Timestamp);
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            if (!result.Success)
            {
                var auditFail = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                auditFail.Log(request.Username, "登录失败", clientIp);
                return Results.Json(new
                {
                    success = false,
                    message = result.Message ?? "用户名或密码错误",
                    retryAfterSeconds = result.RetryAfterSeconds
                }, statusCode: 401);
            }
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(result.Username ?? request.Username, "登录成功", clientIp);
            return Results.Json(new
            {
                success = true,
                token = result.Token,
                username = result.Username,
                expiresAt = result.ExpiresAt
            });
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/auth/time", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            return Results.Json(new { timestamp = authService.GetServerTimestamp() });
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/auth/check", () =>
        {
            return Results.Json(new { success = true, authRequired = true });
        }).RequireHost($"*:{controlPort}");

        // =============== JWT 认证中间件 ===============

        app.Use(async (context, next) =>
        {
            // 仅对控制端口生效
            if (context.Connection.LocalPort != controlPort)
            {
                await next();
                return;
            }

            var path = context.Request.Path.Value ?? "";

            // 跳过非 API 路径（静态文件、SPA 页面）
            if (!path.StartsWith("/api/"))
            {
                await next();
                return;
            }

            // 跳过免认证端点
            if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/time", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/check", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/pcap/ca.crl", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // 验证身份：支持 Bearer Token 和 Basic Auth（curl 快速鉴权）
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"LyWaf\", Bearer";
                await context.Response.WriteAsJsonAsync(new { success = false, message = "未授权访问" });
                return;
            }

            var authService = context.RequestServices.GetRequiredService<IAuthService>();

            // Bearer Token 鉴权
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader["Bearer ".Length..];
                if (!authService.ValidateToken(token))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { success = false, message = "令牌无效或已过期" });
                    return;
                }
                context.Items["Username"] = authService.GetUsername(token) ?? "unknown";
                await next();
                return;
            }

            // Basic Auth 鉴权（curl -u user:pass）
            if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var encoded = authHeader["Basic ".Length..];
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    var colonIndex = decoded.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var basicUser = decoded[..colonIndex];
                        var basicPass = decoded[(colonIndex + 1)..];
                        var result = authService.ValidateCredentials(basicUser, basicPass);
                        if (result.Success)
                        {
                            context.Items["Username"] = basicUser;
                            await next();
                            return;
                        }
                        // 验证失败，返回具体错误
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = result.Message ?? "用户名或密码错误",
                            retryAfterSeconds = result.RetryAfterSeconds
                        });
                        return;
                    }
                }
                catch { /* Base64 解码失败，走到下面的 401 */ }
            }

            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"LyWaf\", Bearer";
            await context.Response.WriteAsJsonAsync(new { success = false, message = "无效的认证方式" });
        });

        // =============== 认证端点（需认证） ===============

        app.MapPost("/api/auth/refresh", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            var token = authHeader?["Bearer ".Length..] ?? "";
            var result = authService.RefreshToken(token);
            if (!result.Success)
            {
                return Results.Json(new { success = false, message = "刷新令牌失败" }, statusCode: 401);
            }
            return Results.Json(new { success = true, token = result.Token, expiresAt = result.ExpiresAt });
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            var token = authHeader?["Bearer ".Length..] ?? "";
            var username = authService.GetUsername(token);
            return Results.Json(new { success = true, username });
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/auth/change-password", async (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var request = await ctx.Request.ReadFromJsonAsync<ChangePasswordRequest>();
            if (request == null || string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
            {
                return Results.Json(new { success = false, message = "密码不能为空" }, statusCode: 400);
            }
            if (request.NewPassword.Length < 6)
            {
                return Results.Json(new { success = false, message = "新密码长度至少6位" }, statusCode: 400);
            }
            var ok = authService.ChangePassword(request.CurrentPassword, request.NewPassword);
            if (!ok)
            {
                return Results.Json(new { success = false, message = "当前密码错误" }, statusCode: 400);
            }
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "修改密码", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, message = "密码修改成功" });
        }).RequireHost($"*:{controlPort}");

        // =============== 业务 API（以下均受认证保护） ===============

        // =============== 错误模板管理 ===============

        // 默认支持的状态码
        int[] errorTemplateCodes = [403, 404, 429, 500, 502, 503];

        // 列出所有错误模板
        app.MapGet("/api/error-templates", (HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;
                var list = new List<object>();

                foreach (var code in errorTemplateCodes)
                {
                    var originalPath = service?.GetOriginalFilePath(code) ?? "";
                    var editPath = service?.GetEditFilePath(code) ?? "";
                    list.Add(new
                    {
                        statusCode = code,
                        hasOriginal = File.Exists(originalPath),
                        hasEdit = File.Exists(editPath)
                    });
                }
                return Results.Json(new { success = true, templates = list });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取模板列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取指定状态码的模板内容
        app.MapGet("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode) ?? "";
                var originalPath = service?.GetOriginalFilePath(statusCode) ?? "";

                string? editContent = null;
                string? originalContent = null;

                if (File.Exists(editPath))
                    editContent = await File.ReadAllTextAsync(editPath, Encoding.UTF8);
                if (File.Exists(originalPath))
                    originalContent = await File.ReadAllTextAsync(originalPath, Encoding.UTF8);

                return Results.Json(new
                {
                    success = true,
                    statusCode,
                    editContent,
                    originalContent,
                    hasEdit = editContent != null,
                    hasOriginal = originalContent != null,
                    // 当前生效内容：优先编辑版
                    activeContent = editContent ?? originalContent ?? ""
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存编辑版模板
        app.MapPost("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                var content = body?.GetValueOrDefault("content");
                if (string.IsNullOrEmpty(content))
                    return Results.Json(new { success = false, message = "模板内容不能为空" }, statusCode: 400);

                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode)
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "templates", $".lywaf.{statusCode}.html");

                // 确保 templates 目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(editPath)!);
                await File.WriteAllTextAsync(editPath, content, Encoding.UTF8);

                // 清除缓存，下次请求使用新内容
                service?.ClearCache();

                return Results.Json(new { success = true, message = "模板已保存" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除编辑版模板（恢复为原始版本）
        app.MapPost("/api/error-templates/{statusCode:int}/revert", (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode)
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "templates", $".lywaf.{statusCode}.html");

                if (File.Exists(editPath))
                {
                    File.Delete(editPath);
                    service?.ClearCache();
                    return Results.Json(new { success = true, message = "已恢复为原始模板" });
                }
                return Results.Json(new { success = true, message = "没有编辑版本需要恢复" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"恢复模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== WAF 自定义规则管理 API ===============

        // 获取所有枚举选项（前端下拉框用）
        app.MapGet("/api/waf-rules/enums", (HttpContext ctx) =>
        {
            return Results.Json(new
            {
                success = true,
                fields = Enum.GetValues<WafMatchField>().Select(e => new { name = e.ToString(), label = GetFieldLabel(e) }),
                operators = Enum.GetValues<WafMatchOperator>().Select(e => new { name = e.ToString(), label = GetOperatorLabel(e) }),
                actions = Enum.GetValues<WafRuleAction>().Select(e => new { name = e.ToString(), label = GetActionLabel(e) }),
                sources = Enum.GetValues<WafRuleSource>().Select(e => new { name = e.ToString(), label = GetSourceLabel(e) }),
            });
        }).RequireHost($"*:{controlPort}");

        // 获取所有规则（合并三个来源：用户 > 配置 > 系统）
        app.MapGet("/api/waf-rules", (HttpContext ctx, IWafRuleService wafRuleService) =>
        {
            var rules = wafRuleService.GetRules();
            return Results.Json(new
            {
                success = true,
                count = rules.Count,
                rules = rules.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    description = r.Description,
                    enabled = r.Enabled,
                    priority = r.Priority,
                    source = r.Source.ToString(),
                    conditions = (r.Conditions ?? []).Select(c => new
                    {
                        field = c.Field.ToString(),
                        fieldName = c.FieldName,
                        @operator = c.Operator.ToString(),
                        value = c.Value,
                        ignoreCase = c.IgnoreCase,
                    }),
                    action = r.Action.ToString(),
                    actionSeconds = r.ActionSeconds,
                    responseCode = r.ResponseCode,
                    createdAt = r.CreatedAt,
                    updatedAt = r.UpdatedAt,
                }),
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单条规则
        app.MapGet("/api/waf-rules/{id}", (string id, IWafRuleService wafRuleService) =>
        {
            var rule = wafRuleService.GetRule(id);
            if (rule == null)
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            return Results.Json(new
            {
                success = true,
                rule = new
                {
                    id = rule.Id,
                    name = rule.Name,
                    description = rule.Description,
                    enabled = rule.Enabled,
                    priority = rule.Priority,
                    source = rule.Source.ToString(),
                    conditions = (rule.Conditions ?? []).Select(c => new
                    {
                        field = c.Field.ToString(),
                        fieldName = c.FieldName,
                        @operator = c.Operator.ToString(),
                        value = c.Value,
                        ignoreCase = c.IgnoreCase,
                    }),
                    action = rule.Action.ToString(),
                    actionSeconds = rule.ActionSeconds,
                    responseCode = rule.ResponseCode,
                    createdAt = rule.CreatedAt,
                    updatedAt = rule.UpdatedAt,
                },
            });
        }).RequireHost($"*:{controlPort}");

        // 创建规则
        app.MapPost("/api/waf-rules", async (HttpContext ctx, IWafRuleService wafRuleService) =>
        {
            try
            {
                var rule = await ctx.Request.ReadFromJsonAsync<WafCustomRule>();
                if (rule == null || string.IsNullOrWhiteSpace(rule.Name))
                    return Results.Json(new { success = false, message = "规则名称不能为空" }, statusCode: 400);


                if (wafRuleService.AddRule(rule))
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"创建WAF规则: {rule.Name}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new { success = true, message = "规则已创建", id = rule.Id });
                }
                return Results.Json(new { success = false, message = "创建失败" }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"创建失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新规则（仅用户规则可编辑）
        app.MapPut("/api/waf-rules/{id}", async (string id, HttpContext ctx, IWafRuleService wafRuleService) =>
        {
            try
            {
                // 检查来源：非用户规则不可编辑
                var existing = wafRuleService.GetRule(id);
                if (existing != null && existing.Source != WafRuleSource.User)
                    return Results.Json(new { success = false, message = "非用户规则不可编辑" }, statusCode: 403);

                var rule = await ctx.Request.ReadFromJsonAsync<WafCustomRule>();
                if (rule == null)
                    return Results.Json(new { success = false, message = "无效的规则数据" }, statusCode: 400);
                rule.Id = id;

                if (wafRuleService.UpdateRule(rule))
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"更新WAF规则: {rule.Name} ({id})", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new { success = true, message = "规则已更新" });
                }
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除规则（仅用户规则可删除）
        app.MapDelete("/api/waf-rules/{id}", (string id, HttpContext ctx, IWafRuleService wafRuleService) =>
        {
            // 检查来源：非用户规则不可删除
            var existing = wafRuleService.GetRule(id);
            if (existing != null && existing.Source != WafRuleSource.User)
                return Results.Json(new { success = false, message = "非用户规则不可删除" }, statusCode: 403);

            var ruleName = existing?.Name ?? id;
            if (wafRuleService.DeleteRule(id))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"删除WAF规则: {ruleName} ({id})", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new { success = true, message = "规则已删除" });
            }
            return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        }).RequireHost($"*:{controlPort}");

        // 切换启用状态
        app.MapPost("/api/waf-rules/{id}/toggle", (string id, HttpContext ctx, IWafRuleService wafRuleService) =>
        {
            if (wafRuleService.ToggleRule(id))
            {
                var rule = wafRuleService.GetRule(id);
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"切换WAF规则: {rule?.Name ?? id} → {(rule?.Enabled == true ? "启用" : "禁用")}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new { success = true, enabled = rule?.Enabled, message = rule?.Enabled == true ? "规则已启用" : "规则已禁用" });
            }
            return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        }).RequireHost($"*:{controlPort}");

        // =============== 拦截事件 API ===============

        // 获取拦截事件
        app.MapGet("/api/intercept-events", (HttpContext ctx) =>
        {
            var ip = ctx.Request.Query["ip"].FirstOrDefault();
            var domain = ctx.Request.Query["domain"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st) ? st : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et) ? et : null;

            var events = SharedData.GetInterceptEvents(ip, domain, startTime, endTime);
            return Results.Json(new
            {
                success = true,
                count = events.Count,
                events = events.Select(e => new
                {
                    sourceIp = e.SourceIp,
                    region = e.Region,
                    city = e.City,
                    application = e.Application,
                    ruleName = e.RuleName,
                    ruleType = e.RuleType,
                    hitCount = e.HitCount,
                    duration = (int)(e.LastHitTime - e.FirstHitTime).TotalMinutes,
                    firstHitTime = e.FirstHitTime,
                    lastHitTime = e.LastHitTime,
                }),
            });
        }).RequireHost($"*:{controlPort}");

        // 清除拦截事件
        app.MapPost("/api/intercept-events/clear", (HttpContext ctx) =>
        {
            SharedData.ClearInterceptEvents();
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "清除拦截事件", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, message = "拦截事件已清除" });
        }).RequireHost($"*:{controlPort}");

        // =============== 拦截明细日志 API ===============

        // 列出拦截日志文件
        app.MapGet("/api/intercept-logs/files", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var files = LogUtil.ListLogFiles(dir, "intercept_*.log");
            return Results.Json(new { success = true, files });
        }).RequireHost($"*:{controlPort}");

        // 读取拦截日志条目
        app.MapGet("/api/intercept-logs/entries", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, message = "无效的文件名" });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, message = $"文件不存在: {fileName}" });

            _ = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault() ?? "0", out var offset);
            _ = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault() ?? "20", out var limit);
            var search = ctx.Request.Query["search"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st2) ? st2 : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et2) ? et2 : null;

            var (entries, total) = await LogUtil.ParseLogFileAsync(filePath, offset, Math.Clamp(limit, 1, 100), search, startTime, endTime);
            return Results.Json(new
            {
                success = true,
                entries,
                total,
                offset,
                limit,
                fileName,
            });
        }).RequireHost($"*:{controlPort}");

        // API 耗时统计数据列表
        app.MapGet("/api/timing/list", (HttpContext ctx) =>
        {
            try
            {
                var snapshot = SharedData.ApiTimings.GetSnapshot();
                
                var data = snapshot.Values
                    .Select(item => new
                    {
                        path = item.Path,
                        method = item.Method,
                        backend = item.Backend,
                        originalHost = item.OriginalHost,
                        requestCount = item.RequestCount,
                        avgTotalTime = Math.Round(item.AvgTotalTime, 2),
                        avgBackendTime = Math.Round(item.AvgBackendTime, 2),
                        avgGatewayTime = Math.Round(item.AvgGatewayTime, 2),
                        minTotalTime = item.MinTotalTime == long.MaxValue ? 0 : item.MinTotalTime,
                        maxTotalTime = item.MaxTotalTime,
                        minBackendTime = item.MinBackendTime == long.MaxValue ? 0 : item.MinBackendTime,
                        maxBackendTime = item.MaxBackendTime,
                        totalTime = item.TotalTime,
                        backendTime = item.BackendTime,
                        statusCodeCounts = item.StatusCodeCounts,
                        lastRequestTime = item.LastRequestTime,
                        // 计算错误率（4xx + 5xx）
                        errorRate = item.RequestCount > 0 
                            ? Math.Round(item.StatusCodeCounts
                                .Where(kv => kv.Key >= 400)
                                .Sum(kv => kv.Value) * 100.0 / item.RequestCount, 2)
                            : 0
                    })
                    .ToList();
                
                // 计算汇总数据
                var totalRequests = data.Sum(x => x.requestCount);
                var totalApis = data.Count;
                var avgTotalTime = totalRequests > 0 
                    ? Math.Round(data.Sum(x => x.totalTime) / (double)totalRequests, 2) 
                    : 0;
                var avgBackendTime = totalRequests > 0 
                    ? Math.Round(data.Sum(x => x.backendTime) / (double)totalRequests, 2) 
                    : 0;
                var avgGatewayTime = avgTotalTime - avgBackendTime;
                
                // 按后端地址统计
                var backendStats = data
                    .Where(x => !string.IsNullOrEmpty(x.backend))
                    .GroupBy(x => x.backend)
                    .Select(g => new
                    {
                        backend = g.Key,
                        apiCount = g.Count(),
                        totalRequests = g.Sum(x => x.requestCount),
                        avgTotalTime = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.totalTime) / (double)g.Sum(x => x.requestCount), 2) 
                            : 0,
                        avgBackendTime = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.backendTime) / (double)g.Sum(x => x.requestCount), 2) 
                            : 0,
                        errorCount = g.Sum(x => x.statusCodeCounts.Where(kv => kv.Key >= 400).Sum(kv => kv.Value)),
                        errorRate = g.Sum(x => x.requestCount) > 0 
                            ? Math.Round(g.Sum(x => x.statusCodeCounts.Where(kv => kv.Key >= 400).Sum(kv => kv.Value)) * 100.0 / g.Sum(x => x.requestCount), 2) 
                            : 0
                    })
                    .OrderByDescending(x => x.errorRate)
                    .ThenByDescending(x => x.avgBackendTime)
                    .ToList();
                
                return Results.Json(new
                {
                    success = true,
                    data,
                    backendStats,
                    summary = new
                    {
                        totalApis,
                        totalRequests,
                        avgTotalTime,
                        avgBackendTime,
                        avgGatewayTime
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // 清除 API 耗时统计数据
        app.MapPost("/api/timing/clear", (HttpContext ctx) =>
        {
            try
            {
                SharedData.ApiTimings.Clear();
                return Results.Json(new { success = true, message = "API 耗时统计数据已清除" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // 重置流量统计数据
        app.MapPost("/api/traffic/reset", (HttpContext ctx) =>
        {
            try
            {
                SharedData.Traffic.Reset();
                SharedData.GeoTraffic.Reset();
                return Results.Json(new { success = true, message = "流量统计数据已重置" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 地理位置流量统计 API ===============

        app.MapGet("/api/geo-traffic/stats", (HttpContext ctx) =>
        {
            try
            {
                var snapshot = SharedData.GeoTraffic.GetSnapshot();
                return Results.Json(new
                {
                    success = true,
                    countryVisits = snapshot.CountryVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    countryIntercepts = snapshot.CountryIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionVisits = snapshot.RegionVisits
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                    regionIntercepts = snapshot.RegionIntercepts
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => new { name = kv.Key, value = kv.Value }),
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 仪表板数据 API ===============
        
        // 获取仪表板概览数据
        app.MapGet("/api/dashboard", (HttpContext ctx, 
            IAccessControlService accessControlService,
            IStatisticService statisticService,
            IProtectService protectService,
            IABTestService abTestService) =>
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var uptime = DateTime.Now - process.StartTime;
                var connectionStats = accessControlService.GetConnectionStats();
                
                // 获取配置选项
                var acOptions = accessControlService.GetOptions();
                var protectOptions = protectService.GetOptions();
                var statisticOptions = statisticService.GetOption();
                
                // 获取流量统计
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                
                // 获取被封禁的IP（合并 ClientFb、CaptchaPending、ClientThrottled）
                var blockedIpList = SharedData.ClientFb.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "blocked",
                        reason = x.Value,
                        remainingSeconds = x.RemainingTime?.TotalSeconds
                    })
                    .Concat(SharedData.CaptchaPending.GetValidItemsWithExpiry()
                        .Select(x => new
                        {
                            ip = x.Key,
                            type = "captcha",
                            reason = $"验证码待验证: {x.Value.RuleName}",
                            remainingSeconds = x.RemainingTime?.TotalSeconds
                        }))
                    .Concat(SharedData.ClientThrottled.GetValidItemsWithExpiry()
                        .Select(x => new
                        {
                            ip = x.Key,
                            type = "throttled",
                            reason = $"带宽限速: {x.Value.EveryCapacity / 1024}KB/s",
                            remainingSeconds = x.RemainingTime?.TotalSeconds
                        }))
                    .Concat(SharedData.IpLogTargets.GetValidItemsWithExpiry()
                        .Select(x =>
                        {
                            var (exists, sizeBytes, _) = IpRequestLogger.GetLogFileInfo(x.Key);
                            return new
                            {
                                ip = x.Key,
                                type = "log",
                                reason = exists ? $"请求日志记录中 ({sizeBytes / 1024}KB)" : "请求日志记录中",
                                remainingSeconds = x.RemainingTime?.TotalSeconds
                            };
                        }))
                    .ToList();

                // 获取最近5分钟内的客户端
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var threshold = now - (5 * 60 * 1000);
                var recentClients = SharedData.ClientStas.GetSnapshot()
                    .Where(kv => kv.Value.LastAccessTime >= threshold)
                    .Select(kv => new
                    {
                        ip = kv.Key,
                        lastAccessTime = DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastAccessTime).LocalDateTime
                    })
                    .OrderByDescending(x => x.lastAccessTime)
                    .Take(50)
                    .ToList();
                
                // 格式化运行时间
                var uptimeStr = uptime.Days > 0 
                    ? $"{uptime.Days}天 {uptime.Hours}小时 {uptime.Minutes}分钟" 
                    : uptime.Hours > 0 
                        ? $"{uptime.Hours}小时 {uptime.Minutes}分钟 {uptime.Seconds}秒"
                        : $"{uptime.Minutes}分钟 {uptime.Seconds}秒";

                return Results.Json(new
                {
                    success = true,
                    system = new
                    {
                        uptime = uptimeStr,
                        memory = process.WorkingSet64 / (1024 * 1024),
                        totalConnections = ConnectionTracker.GetActiveCount(),
                        httpConnections = ConnectionTracker.GetHttpCount(),
                        wsConnections = ConnectionTracker.GetWebSocketCount(),
                        blockedIpCount = blockedIpList.Count,
                        processStartTime = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        uniqueIps = connectionStats.ConnectionsPerIp.Count
                    },
                    traffic = new
                    {
                        totalRequests = trafficSnapshot.TotalRequests,
                        pageViews = trafficSnapshot.PageViews,
                        uniqueVisitors = trafficSnapshot.UniqueVisitors,
                        uniqueIps = trafficSnapshot.UniqueIps,
                        interceptCount = trafficSnapshot.InterceptCount,
                        attackIps = trafficSnapshot.AttackIps,
                        error4xxCount = trafficSnapshot.Error4xxCount,
                        error4xxRate = trafficSnapshot.Error4xxRate,
                        intercept4xxCount = trafficSnapshot.Intercept4xxCount,
                        intercept4xxRate = trafficSnapshot.Intercept4xxRate,
                        error5xxCount = trafficSnapshot.Error5xxCount,
                        error5xxRate = trafficSnapshot.Error5xxRate,
                        startTime = trafficSnapshot.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    },
                    features = new
                    {
                        ipControl = acOptions.IpControl.Enabled,
                        geoControl = acOptions.GeoControl.Enabled,
                        connectionLimit = acOptions.ConnectionLimit.Enabled,
                        wafArgs = protectOptions.OpenArgsCheck,
                        wafPost = protectOptions.OpenPostCheck,
                        ccProtection = statisticOptions.LimitCc.Count > 0 || statisticService.GetLimitCcRules().Count > 0
                    },
                    recentClients,
                    whitelist = accessControlService.GetWhitelist(),
                    blacklist = accessControlService.GetBlacklist(),
                    geoAccess = new
                    {
                        allowCountries = accessControlService.GetAllowCountries(),
                        allowRegions = accessControlService.GetAllowRegions(),
                        denyCountries = accessControlService.GetDenyCountries(),
                        denyRegions = accessControlService.GetDenyRegions()
                    },
                    wafRules = new
                    {
                        args = protectService.GetArgsRegexList(),
                        post = protectService.GetPostRegexList(),
                        argsFileCount = protectService.GetArgsFilePatternCount(),
                        postFileCount = protectService.GetPostFilePatternCount()
                    },
                    ccRules = statisticService.GetLimitCcRules().Select(r => new
                    {
                        path = r.Path,
                        period = r.Period,
                        limitNum = r.LimitNum,
                        fbTime = r.FbTime.TotalSeconds
                    }),
                    blockedIps = blockedIpList,
                    abTests = abTestService.GetAllConfigs().Select(c => new
                    {
                        testId = c.Key,
                        name = c.Value.Name,
                        enabled = c.Value.Enabled,
                        mode = c.Value.Mode.ToString(),
                        variants = c.Value.Variants
                    }),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取流量统计数据
        app.MapGet("/api/traffic/stats", (HttpContext ctx) =>
        {
            try
            {
                var trafficSnapshot = SharedData.Traffic.GetSnapshot();
                return Results.Json(new
                {
                    success = true,
                    totalRequests = trafficSnapshot.TotalRequests,
                    pageViews = trafficSnapshot.PageViews,
                    uniqueVisitors = trafficSnapshot.UniqueVisitors,
                    uniqueIps = trafficSnapshot.UniqueIps,
                    interceptCount = trafficSnapshot.InterceptCount,
                    attackIps = trafficSnapshot.AttackIps,
                    error4xxCount = trafficSnapshot.Error4xxCount,
                    error4xxRate = trafficSnapshot.Error4xxRate,
                    intercept4xxCount = trafficSnapshot.Intercept4xxCount,
                    intercept4xxRate = trafficSnapshot.Intercept4xxRate,
                    error5xxCount = trafficSnapshot.Error5xxCount,
                    error5xxRate = trafficSnapshot.Error5xxRate,
                    startTime = trafficSnapshot.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 安全态势统计数据
        app.MapGet("/api/security/stats", (HttpContext ctx) =>
        {
            try
            {
                var hoursStr = ctx.Request.Query["hours"].FirstOrDefault();
                var hours = int.TryParse(hoursStr, out var h) ? h : 24;
                
                var snapshot = SharedData.Security.GetSnapshot();
                var timeSlots = SharedData.Security.GetTimeSlots(hours);
                var topAttackSources = SharedData.Security.GetTopAttackSources(10);
                
                // 各类型的 Top IP
                var topWafSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.WafIntercept, 5);
                var topCcSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.CcAttack, 5);
                var topBlacklistSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.BlacklistBlock, 5);
                var topGeoSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.GeoBlock, 5);
                var topCrawlerSources = SharedData.Security.GetTopAttackSourcesByType(SecurityEventType.CrawlerDetect, 5);
                
                return Results.Json(new
                {
                    snapshot = new
                    {
                        startTime = snapshot.StartTime,
                        wafInterceptCount = snapshot.WafInterceptCount,
                        blacklistBlockCount = snapshot.BlacklistBlockCount,
                        ccAttackCount = snapshot.CcAttackCount,
                        crawlerDetectCount = snapshot.CrawlerDetectCount,
                        geoBlockCount = snapshot.GeoBlockCount,
                        rateLimitCount = snapshot.RateLimitCount,
                        totalInterceptCount = snapshot.TotalInterceptCount,
                        uniqueAttackIps = snapshot.UniqueAttackIps
                    },
                    timeSlots = timeSlots.Select(s => new
                    {
                        time = s.Time,
                        wafIntercept = s.WafIntercept,
                        blacklistBlock = s.BlacklistBlock,
                        ccAttack = s.CcAttack,
                        crawlerDetect = s.CrawlerDetect,
                        geoBlock = s.GeoBlock,
                        rateLimit = s.RateLimit,
                        total = s.Total
                    }),
                    topAttackSources = topAttackSources.Select(s => new { ip = s.Ip, count = s.Count }),
                    topWafSources = topWafSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topCcSources = topCcSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topBlacklistSources = topBlacklistSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topGeoSources = topGeoSources.Select(s => new { ip = s.Item1, count = s.Item2 }),
                    topCrawlerSources = topCrawlerSources.Select(s => new { ip = s.Item1, count = s.Item2 })
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        // QPS 历史数据（实时用内存，历史用 DuckDB）
        app.MapGet("/api/qps/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                var granularity = ctx.Request.Query["granularity"].FirstOrDefault() ?? "5s";
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();

                // 如果指定了 from/to，从 DuckDB 查历史
                if (fromStr != null || toStr != null)
                {
                    if (!queryService.IsEnabled)
                        return Results.Json(new { success = false, message = "DuckDB 未启用" });

                    var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddHours(-24);
                    var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;
                    var dbGranularity = granularity switch
                    {
                        "5s" => "1min",
                        "1min" => "1min",
                        "1hour" => "5min",
                        _ => "1min"
                    };
                    var rows = queryService.GetQpsHistory(from, to, dbGranularity);
                    var stepSeconds = dbGranularity == "5min" ? 300.0 : 60.0;
                    var data = rows.Select(r => new
                    {
                        time = r.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"),
                        qps = Math.Round(r.RequestCount / stepSeconds, 2),
                        requestCount = r.RequestCount
                    });
                    return Results.Json(new { success = true, data, granularity, source = "duckdb" });
                }

                // 否则从内存取实时数据
                var memData = SharedData.Qps.GetHistory(granularity);
                return Results.Json(new { success = true, data = memData, granularity, source = "memory" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 带宽历史
        app.MapGet("/api/bandwidth/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                var granularity = ctx.Request.Query["granularity"].FirstOrDefault() ?? "5s";
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();

                if (fromStr != null || toStr != null)
                {
                    if (!queryService.IsEnabled)
                        return Results.Json(new { success = false, message = "DuckDB 未启用" });

                    var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddHours(-24);
                    var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;
                    var dbGranularity = granularity switch
                    {
                        "5s" => "1min",
                        "1min" => "1min",
                        "1hour" => "5min",
                        _ => "1min"
                    };
                    var stepSeconds = dbGranularity == "5min" ? 300.0 : 60.0;
                    var rows = queryService.GetBandwidthHistory(from, to, dbGranularity);
                    var data = rows.Select(r => new
                    {
                        time = r.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"),
                        inboundBytes = r.InboundBytes,
                        outboundBytes = r.OutboundBytes,
                        inboundRate = Math.Round(r.InboundBytes / stepSeconds, 2),
                        outboundRate = Math.Round(r.OutboundBytes / stepSeconds, 2),
                    });
                    return Results.Json(new { success = true, data, granularity, source = "duckdb" });
                }

                var memData = SharedData.Bandwidth.GetHistory(granularity);
                return Results.Json(new { success = true, data = memData, granularity, source = "memory" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 重置安全态势统计数据
        app.MapPost("/api/security/reset", (HttpContext ctx) =>
        {
            try
            {
                SharedData.Security.Reset();
                return Results.Json(new { success = true, message = "安全态势统计数据已重置" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            return Results.Json(new
            {
                status = "running",
                pid = Environment.ProcessId,
                uptime = DateTime.Now - Process.GetCurrentProcess().StartTime,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/info", (HttpContext ctx) =>
        {
            var process = Process.GetCurrentProcess();
            return Results.Json(new
            {
                pid = process.Id,
                name = process.ProcessName,
                startTime = process.StartTime,
                memoryMB = process.WorkingSet64 / (1024 * 1024),
                threads = process.Threads.Count
            });
        }).RequireHost($"*:{controlPort}");

        // =============== 配置概览 API ===============
        app.MapGet("/api/overview", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                // 控制台监听
                var cl = wafInfos.GetControlListen();

                // 证书信息
                var certs = wafInfos.Certs.Select(c => new
                {
                    host = c.Host,
                    pemFile = Path.GetFileName(c.PemFile),
                    hasKey = !string.IsNullOrEmpty(c.KeyFile)
                }).ToList();

                // 收集所有路由信息（含 hosts 和 source）
                var rpRoutes = config.GetSection("ReverseProxy:Routes");
                var allRoutes = new List<(string RouteId, string ClusterId, string Path, List<string> Hosts, string Source)>();
                foreach (var rs in rpRoutes.GetChildren())
                {
                    var matchSection = rs.GetSection("Match");
                    var hosts = new List<string>();
                    foreach (var h in matchSection.GetSection("Hosts").GetChildren())
                    {
                        if (h.Value != null) hosts.Add(h.Value);
                    }
                    var source = DetectConfigSource(config, $"ReverseProxy:Routes:{rs.Key}");
                    var sourceText = source == ConfigSource.PatchConfig ? "patch" : "original";
                    allRoutes.Add((rs.Key, rs["ClusterId"] ?? "", matchSection["Path"] ?? "", hosts, sourceText));
                }

                // 收集所有集群信息
                var rpClusters = config.GetSection("ReverseProxy:Clusters");
                var allClusters = new Dictionary<string, object>();
                foreach (var cs in rpClusters.GetChildren())
                {
                    var destinations = new List<object>();
                    foreach (var dest in cs.GetSection("Destinations").GetChildren())
                    {
                        destinations.Add(new
                        {
                            id = dest.Key,
                            address = dest["Address"] ?? ""
                        });
                    }
                    allClusters[cs.Key] = new
                    {
                        id = cs.Key,
                        policy = cs["LoadBalancingPolicy"] ?? "RoundRobin",
                        destinationCount = destinations.Count,
                        destinations
                    };
                }

                // 辅助方法：解析 host 模式中的端口
                static int? ExtractPort(string pattern)
                {
                    if (string.IsNullOrEmpty(pattern)) return null;
                    // IPv6 格式 [::1]:8080
                    if (pattern.StartsWith('['))
                    {
                        var bracketEnd = pattern.IndexOf(']');
                        if (bracketEnd >= 0 && bracketEnd + 1 < pattern.Length && pattern[bracketEnd + 1] == ':')
                        {
                            if (int.TryParse(pattern[(bracketEnd + 2)..], out var p)) return p;
                        }
                        return null;
                    }
                    // 普通格式 host:port
                    var colonIdx = pattern.LastIndexOf(':');
                    if (colonIdx > 0 && int.TryParse(pattern[(colonIdx + 1)..], out var port))
                    {
                        return port;
                    }
                    return null;
                }

                // 判断路由是否匹配某个监听端口
                static bool RouteMatchesPort(string routeId, List<string> routeHosts, int listenPort)
                {
                    if (routeHosts.Count == 0)
                    {
                        // 从路由 ID 中提取 listen_XXXX_default 的端口号（兼容 simpleres_listen_XXXX_default 等前缀）
                        var listenIdx = routeId.IndexOf("listen_", StringComparison.Ordinal);
                        if (listenIdx >= 0 && routeId.EndsWith("_default"))
                        {
                            var portStr = routeId[(listenIdx + "listen_".Length)..^"_default".Length];
                            if (int.TryParse(portStr, out var routePort))
                                return routePort == listenPort;
                        }
                        // 其他无 Hosts 限制的路由匹配所有端口
                        return true;
                    }
                    foreach (var h in routeHosts)
                    {
                        var p = ExtractPort(h);
                        if (p == listenPort) return true;
                    }
                    return false;
                }

                // 确定路由的服务类型
                static string GetServiceType(string routeId)
                {
                    if (routeId.StartsWith("fileserver_", StringComparison.OrdinalIgnoreCase)) return "fileserver";
                    if (routeId.StartsWith("simpleres_", StringComparison.OrdinalIgnoreCase)) return "simpleres";
                    return "proxy";
                }

                // 读取补丁中的监听端口（包含尚未重启生效的新端口）
                var patchListenIndices = new HashSet<string>();
                var pendingPatchListens = new List<(string Host, int Port, bool IsHttps, int? AutoHttpsPort)>();
                try
                {
                    var patchPath = LbPatchConfig.GetPatchFilePath();
                    if (File.Exists(patchPath))
                    {
                        var patchJson = File.ReadAllText(patchPath, Encoding.UTF8);
                        using var patchDoc = System.Text.Json.JsonDocument.Parse(patchJson);
                        if (patchDoc.RootElement.TryGetProperty("Listens", out var patchListensEl))
                        {
                            foreach (var prop in patchListensEl.EnumerateObject())
                            {
                                patchListenIndices.Add(prop.Name);
                                // 索引 >= wafInfos.Listens.Count 表示是新增的、尚未重启生效的端口
                                if (int.TryParse(prop.Name, out var pIdx) && pIdx >= wafInfos.Listens.Count)
                                {
                                    var pHost = prop.Value.TryGetProperty("Host", out var hEl) ? hEl.GetString() ?? "0.0.0.0" : "0.0.0.0";
                                    var pPort = prop.Value.TryGetProperty("Port", out var pEl) ? pEl.GetInt32() : 0;
                                    var pHttps = prop.Value.TryGetProperty("IsHttps", out var sEl) && sEl.GetBoolean();
                                    int? pAutoHttps = prop.Value.TryGetProperty("AutoHttpsPort", out var aEl) && aEl.ValueKind == System.Text.Json.JsonValueKind.Number
                                        ? aEl.GetInt32() : null;
                                    if (pPort > 0) pendingPatchListens.Add((pHost, pPort, pHttps, pAutoHttps));
                                }
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                // 为每个监听端口匹配关联的路由和服务
                var listens = wafInfos.Listens.Select((l, idx) =>
                {
                    var boundRoutes = allRoutes
                        .Where(r => RouteMatchesPort(r.RouteId, r.Hosts, l.Port))
                        .Select(r => new
                        {
                            routeId = r.RouteId,
                            clusterId = r.ClusterId,
                            path = r.Path,
                            hosts = r.Hosts,
                            serviceType = GetServiceType(r.RouteId),
                            source = r.Source
                        })
                        .ToList();

                    return new
                    {
                        host = l.Host,
                        port = l.Port,
                        isHttps = l.IsHttps,
                        autoHttpsPort = l.AutoHttpsPort,
                        routes = boundRoutes,
                        source = patchListenIndices.Contains(idx.ToString()) ? "patch" : "original"
                    };
                }).ToList();

                // 追加尚未重启生效的补丁端口（已在补丁中但不在 wafInfos.Listens 中）
                foreach (var pl in pendingPatchListens)
                {
                    var boundRoutes = allRoutes
                        .Where(r => RouteMatchesPort(r.RouteId, r.Hosts, pl.Port))
                        .Select(r => new
                        {
                            routeId = r.RouteId,
                            clusterId = r.ClusterId,
                            path = r.Path,
                            hosts = r.Hosts,
                            serviceType = GetServiceType(r.RouteId),
                            source = r.Source
                        })
                        .ToList();

                    listens.Add(new
                    {
                        host = pl.Host,
                        port = pl.Port,
                        isHttps = pl.IsHttps,
                        autoHttpsPort = pl.AutoHttpsPort,
                        routes = boundRoutes,
                        source = "patch"
                    });
                }

                return Results.Json(new
                {
                    success = true,
                    listens,
                    controlListen = new { host = cl.Host, port = cl.Port },
                    certs,
                    clusters = allClusters.Values.ToList(),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取概览数据失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/config", (HttpContext ctx, IConfiguration config) =>
        {
            var configDict = new Dictionary<string, object?>();
            foreach (var section in config.GetChildren())
            {
                if (AllowedConfigKeys.Contains(section.Key))
                {
                    configDict[section.Key] = GetSectionValue(section);
                }
            }
            
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(configDict);
            
            return Results.Text(yaml, "text/yaml", Encoding.UTF8);
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/stop", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            // 停止应用会触发插件系统停止（包括 AnalysisPlugin）
            lifetime.StopApplication();
            return Results.Json(new { message = "服务正在停止..." });
        }).RequireHost($"*:{controlPort}");
        
        app.MapGet("/api/reload", (HttpContext ctx, IConfiguration config) =>
        {
            if (config is not IConfigurationRoot configRoot)
            {
                return Results.Json(new { success = false, message = "配置重载失败：不支持的配置类型" }, statusCode: 500);
            }

            configRoot.Reload();

            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "重载配置", ctx.Connection.RemoteIpAddress?.ToString() ?? "");

            // 检测端口是否变化（仅通知前端，由用户确认后调用 /api/restart 重启）
            var newWafInfos = new WafInfoOptions();
            config.GetSection("WafInfos").Bind(newWafInfos);
            var oldPorts = wafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
            var newPorts = newWafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
            var portsChanged = !oldPorts.SequenceEqual(newPorts);

            return Results.Json(new
            {
                success = true,
                message = portsChanged
                    ? "配置已重新加载，检测到端口变更，需要重启服务才能生效"
                    : "配置已重新加载",
                portsChanged,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 重启服务（用于端口变更后由前端确认触发）
        app.MapGet("/api/restart", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", "重启服务", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            SharedData.RestartRequested = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // 等待响应发送完毕
                lifetime.StopApplication();
            });
            return Results.Json(new
            {
                success = true,
                message = "服务正在重启...",
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== 配置文件管理 API ===============

        // 获取配置文件内容（优先返回草稿内容，同时返回原始文件内容）
        app.MapGet("/api/config/file", (HttpContext ctx) =>
        {
            try
            {
                var filePath = SharedData.ConfigFilePath;
                if (!File.Exists(filePath))
                {
                    return Results.Json(new { success = false, message = $"配置文件不存在: {filePath}" }, statusCode: 404);
                }

                var originalContent = File.ReadAllText(filePath, Encoding.UTF8);
                var fileName = Path.GetFileName(filePath);
                var format = filePath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase) ? "ly" : "yaml";

                // 检查是否存在草稿文件
                var dir = Path.GetDirectoryName(filePath) ?? ".";
                var ext = Path.GetExtension(filePath);
                var draftPath = Path.Combine(dir, $".lywaf.draft{ext}");
                var draftExists = File.Exists(draftPath);
                string? draftContent = null;
                if (draftExists)
                {
                    draftContent = File.ReadAllText(draftPath, Encoding.UTF8);
                }

                return Results.Json(new
                {
                    success = true,
                    fileName,
                    format,
                    content = originalContent,
                    draftContent,
                    hasDraft = draftExists,
                    draftLoaded = SharedData.ExistsDraftConfig
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"读取配置文件失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存配置文件内容（保存到草稿文件，不覆盖原始配置）
        app.MapPost("/api/config/file", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<SaveConfigRequest>();
                if (request == null || string.IsNullOrEmpty(request.Content))
                {
                    return Results.Json(new { success = false, message = "请求内容为空" }, statusCode: 400);
                }

                var originalPath = SharedData.ConfigFilePath;
                var isLy = originalPath.EndsWith(".ly", StringComparison.OrdinalIgnoreCase);

                // 如果是 .ly 文件，先校验语法
                if (isLy)
                {
                    try
                    {
                        LyConfigParser.Parse(request.Content);
                    }
                    catch (LyConfigException ex)
                    {
                        return Results.Json(new { success = false, message = $"语法错误: {ex.Message}" }, statusCode: 400);
                    }
                }

                // 保存到草稿文件（不覆盖原始配置文件）
                var dir = Path.GetDirectoryName(originalPath) ?? ".";
                var ext = Path.GetExtension(originalPath);
                var draftPath = Path.Combine(dir, $".lywaf.draft{ext}");

                await File.WriteAllTextAsync(draftPath, request.Content, Encoding.UTF8);

                var message = $"草稿已保存到 {Path.GetFileName(draftPath)}";
                var portsChanged = false;

                // 如果需要重载配置，直接 Reload
                // LyConfigProvider / DraftAwareYamlProvider 会自动检测草稿文件并优先读取
                if (request.Reload)
                {
                    if (config is IConfigurationRoot configRoot)
                    {
                        configRoot.Reload();
                        message = "草稿已保存，配置已重新加载";

                        // 检测端口是否变化（仅通知前端，由用户确认后调用 /api/restart 重启）
                        var newWafInfos = new WafInfoOptions();
                        config.GetSection("WafInfos").Bind(newWafInfos);
                        var oldPorts = wafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
                        var newPorts = newWafInfos.Listens.Select(l => (l.Host, l.Port, l.IsHttps)).OrderBy(x => x).ToList();
                        portsChanged = !oldPorts.SequenceEqual(newPorts);

                        if (portsChanged)
                        {
                            message = "草稿已保存，配置已重新加载，检测到端口变更，需要重启服务才能生效";
                        }
                    }
                }

                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, request.Reload ? "保存并重载配置" : "保存配置草稿", clientIp);

                return Results.Json(new
                {
                    success = true,
                    message,
                    portsChanged,
                    draftFile = Path.GetFileName(draftPath)
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 将 .ly 内容转换为 YAML 预览
        app.MapPost("/api/config/convert", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ConvertConfigRequest>();
                if (request == null || string.IsNullOrEmpty(request.Content))
                {
                    return Results.Json(new { success = false, message = "请求内容为空" }, statusCode: 400);
                }

                // 收集环境变量用于变量替换
                var variables = new Dictionary<string, string>();
                foreach (var key in Environment.GetEnvironmentVariables().Keys)
                {
                    var keyStr = key.ToString()!;
                    variables[keyStr] = Environment.GetEnvironmentVariable(keyStr) ?? "";
                }

                // 分步转换：先得到 appsettings 字典，合并补丁后再输出 YAML
                var config = LyConfigParser.Parse(request.Content, variables);
                var appSettings = LyToAppSettingsConverter.TransformToAppSettings(config);

                // 读取补丁文件并合并到 appSettings
                try
                {
                    var patchPath = LbPatchConfig.GetPatchFilePath();
                    if (File.Exists(patchPath))
                    {
                        var patchJson = await File.ReadAllTextAsync(patchPath, Encoding.UTF8);
                        using var patchDoc = System.Text.Json.JsonDocument.Parse(patchJson);
                        var root = patchDoc.RootElement;

                        // 合并 Routes 补丁 → ReverseProxy.Routes
                        if (root.TryGetProperty("Routes", out var routesEl))
                        {
                            var rp = EnsureNestedDict(appSettings, "ReverseProxy");
                            var routes = EnsureNestedDict(rp, "Routes");
                            foreach (var route in routesEl.EnumerateObject())
                            {
                                routes[route.Name] = JsonElementToYamlDict(route.Value);
                            }
                        }

                        // 合并 Clusters 补丁 → ReverseProxy.Clusters
                        if (root.TryGetProperty("Clusters", out var clustersEl))
                        {
                            var rp = EnsureNestedDict(appSettings, "ReverseProxy");
                            var clusters = EnsureNestedDict(rp, "Clusters");
                            foreach (var cluster in clustersEl.EnumerateObject())
                            {
                                clusters[cluster.Name] = JsonElementToYamlDict(cluster.Value);
                            }
                        }

                        // 合并 FileServer 补丁 → FileServer.Items
                        if (root.TryGetProperty("FileServer", out var fsEl))
                        {
                            var fs = EnsureNestedDict(appSettings, "FileServer");
                            var items = EnsureNestedDict(fs, "Items");
                            foreach (var item in fsEl.EnumerateObject())
                            {
                                items[item.Name] = JsonElementToYamlDict(item.Value);
                            }
                        }

                        // 合并 SimpleRes 补丁 → SimpleRes.Items
                        if (root.TryGetProperty("SimpleRes", out var srEl))
                        {
                            var sr = EnsureNestedDict(appSettings, "SimpleRes");
                            var items = EnsureNestedDict(sr, "Items");
                            foreach (var item in srEl.EnumerateObject())
                            {
                                items[item.Name] = JsonElementToYamlDict(item.Value);
                            }
                        }

                        // 合并 Listens 补丁 → WafInfos.Listens
                        if (root.TryGetProperty("Listens", out var listensEl))
                        {
                            var waf = EnsureNestedDict(appSettings, "WafInfos");
                            // Listens 是一个列表
                            List<object> listensList;
                            if (waf.TryGetValue("Listens", out var existingListens) && existingListens is List<object> el)
                            {
                                listensList = el;
                            }
                            else
                            {
                                listensList = new List<object>();
                                waf["Listens"] = listensList;
                            }

                            foreach (var listen in listensEl.EnumerateObject())
                            {
                                if (int.TryParse(listen.Name, out var idx))
                                {
                                    var listenDict = JsonElementToYamlDict(listen.Value);
                                    if (idx < listensList.Count)
                                    {
                                        // 覆盖已有项
                                        if (listensList[idx] is Dictionary<string, object> existing)
                                        {
                                            foreach (var kv in listenDict)
                                                existing[kv.Key] = kv.Value;
                                        }
                                        else
                                        {
                                            listensList[idx] = listenDict;
                                        }
                                    }
                                    else
                                    {
                                        // 补齐空位并追加
                                        while (listensList.Count < idx)
                                            listensList.Add(new Dictionary<string, object>());
                                        listensList.Add(listenDict);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 合并补丁失败时继续使用基础配置
                }

                var yaml = LyToYamlConverter.DictToYaml(appSettings);
                return Results.Json(new { success = true, yaml });
            }
            catch (LyConfigException ex)
            {
                return Results.Json(new { success = false, message = $"转换失败（语法错误）: {ex.Message}" }, statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"转换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/statistics", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var queryIp = ctx.Request.Query["ip"].FirstOrDefault();
            var isFilterByIp = !string.IsNullOrEmpty(queryIp);

            var connectionStats = accessControlService.GetConnectionStats();

            // 如果指定了IP，只返回该IP的连接统计
            if (isFilterByIp)
            {
                connectionStats = new ConnectionStats
                {
                    TotalConnections = connectionStats.TotalConnections,
                    ConnectionsPerIp = connectionStats.ConnectionsPerIp
                        .Where(kv => kv.Key == queryIp)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    ConnectionsPerDestination = connectionStats.ConnectionsPerDestination,
                    ConnectionsPerPath = connectionStats.ConnectionsPerPath
                };
            }

            // 获取客户端统计数据
            var clientStatsSnapshot = SharedData.ClientStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientStatsSnapshot = clientStatsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientStats = clientStatsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    totalCount = kv.Value.CountTime.Count,
                    totalTime = kv.Value.CountTime.UseTime,
                    avgTime = kv.Value.CountTime.Average,
                    urlStats = kv.Value.UrlCostTime.Select(u => new
                    {
                        path = u.Key,
                        count = u.Value.Count,
                        totalTime = u.Value.UseTime,
                        avgTime = u.Value.Average
                    }).OrderByDescending(x => x.count).Take(10)
                })
                .OrderByDescending(x => x.totalCount)
                .Take(isFilterByIp ? int.MaxValue : 100)
                .ToList();

            // 如果指定了IP，不返回请求路径统计和目标服务器统计
            List<object>? requestStats = null;
            List<object>? destinationStats = null;

            if (!isFilterByIp)
            {
                // 获取请求路径统计数据
                requestStats = SharedData.ReqStas.GetSnapshot()
                    .Select(kv => new
                    {
                        path = kv.Key,
                        totalCount = kv.Value.CountTime.Count,
                        totalTime = kv.Value.CountTime.UseTime,
                        avgTime = kv.Value.CountTime.Average
                    })
                    .OrderByDescending(x => x.totalCount)
                    .Take(50)
                    .Cast<object>()
                    .ToList();

                // 获取目标服务器统计数据
                destinationStats = SharedData.DestStas.GetSnapshot()
                    .Select(kv => new
                    {
                        destination = kv.Key,
                        totalCount = kv.Value.CountTime.Count,
                        totalTime = kv.Value.CountTime.UseTime,
                        avgTime = kv.Value.CountTime.Average
                    })
                    .OrderByDescending(x => x.totalCount)
                    .Cast<object>()
                    .ToList();
            }

            // 获取被封禁的IP（如果指定了IP，只返回该IP是否被封禁）
            var blockedIpsSnapshot = SharedData.ClientFb.GetSnapshot();
            
            if (isFilterByIp)
            {
                blockedIpsSnapshot = blockedIpsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var blockedIps = blockedIpsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    reason = kv.Value
                })
                .ToList();

            // 获取CC限制统计（如果指定了IP，只返回该IP相关的）
            var ccLimitStatsSnapshot = SharedData.LimitCcStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                ccLimitStatsSnapshot = ccLimitStatsSnapshot
                    .Where(kv => kv.Key.Contains(queryIp ?? ""))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var ccLimitStats = ccLimitStatsSnapshot
                .Select(kv => new
                {
                    key = kv.Key,
                    count = kv.Value
                })
                .OrderByDescending(x => x.count)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            // 获取客户端访问次数（如果指定了IP，只返回该IP的）
            var clientVisitsSnapshot = SharedData.NewClientVisits.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientVisitsSnapshot = clientVisitsSnapshot
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientVisits = clientVisitsSnapshot
                .Select(kv => new
                {
                    ip = kv.Key,
                    visits = kv.Value
                })
                .OrderByDescending(x => x.visits)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            // 获取客户端最后访问时间（从 ClientStas 的 LastAccessTime 字段获取）
            var clientStasForLastAccess = SharedData.ClientStas.GetSnapshot();
            
            if (isFilterByIp)
            {
                clientStasForLastAccess = clientStasForLastAccess
                    .Where(kv => kv.Key == queryIp)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var clientLastAccess = clientStasForLastAccess
                .Select(kv => new
                {
                    ip = kv.Key,
                    lastAccessTime = DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.LastAccessTime).LocalDateTime
                })
                .OrderByDescending(x => x.lastAccessTime)
                .Take(isFilterByIp ? int.MaxValue : 50)
                .ToList();

            var result = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now,
                ["summary"] = new
                {
                    totalClients = isFilterByIp ? (clientStats.Count > 0 ? 1 : 0) : SharedData.ClientStas.Count,
                    totalBlockedIps = isFilterByIp ? (blockedIps.Count > 0 ? 1 : 0) : SharedData.ClientFb.Count,
                    totalConnections = ConnectionTracker.GetActiveCount(),
                    httpConnections = ConnectionTracker.GetHttpCount(),
                    wsConnections = ConnectionTracker.GetWebSocketCount(),
                    filteredIp = isFilterByIp ? queryIp : null
                },
                ["connections"] = connectionStats,
                ["clientStats"] = clientStats,
                ["blockedIps"] = blockedIps,
                ["ccLimitStats"] = ccLimitStats,
                ["clientVisits"] = clientVisits,
                ["clientLastAccess"] = clientLastAccess
            };

            // 只有在未指定IP时才添加这些字段
            if (!isFilterByIp)
            {
                result["requestStats"] = requestStats;
                result["destinationStats"] = destinationStats;
            }

            return Results.Json(result);
        }).RequireHost($"*:{controlPort}");

        // =============== 功能状态切换 API ===============
        
        // 切换 IP 访问控制状态
        app.MapPost("/api/feature/ip-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().IpControl.Enabled;
                accessControlService.SetIpControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "ip-control",
                    enabled = enabled,
                    message = enabled ? "IP 访问控制已启用" : "IP 访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换地理位置访问控制状态
        app.MapPost("/api/feature/geo-control/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().GeoControl.Enabled;
                accessControlService.SetGeoControlEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "geo-control",
                    enabled = enabled,
                    message = enabled ? "地理位置访问控制已启用" : "地理位置访问控制已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // ==================== 连接限制 API ====================

        // 获取连接限制配置
        app.MapGet("/api/connection-limit/config", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var config = options.ConnectionLimit;
            var stats = accessControlService.GetConnectionStats();
            return Results.Json(new
            {
                success = true,
                enabled = config.Enabled,
                maxConnectionsPerIp = config.MaxConnectionsPerIp,
                maxConnectionsPerDestination = config.MaxConnectionsPerDestination,
                maxTotalConnections = config.MaxTotalConnections,
                rejectStatusCode = config.RejectStatusCode,
                pathLimits = config.PathLimits,
                stats = new
                {
                    totalConnections = stats.TotalConnections,
                    connectionsPerIp = stats.ConnectionsPerIp.Count,
                    connectionsPerDestination = stats.ConnectionsPerDestination.Count
                }
            });
        }).RequireHost($"*:{controlPort}");

        // 切换连接限制启用状态
        app.MapPost("/api/connection-limit/toggle", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !accessControlService.GetOptions().ConnectionLimit.Enabled;
                accessControlService.SetConnectionLimitEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    enabled,
                    message = enabled ? "连接限制已启用" : "连接限制已禁用"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新连接限制配置
        app.MapPost("/api/connection-limit/update", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateConnectionLimitRequest>();
                if (request == null) return Results.Json(new { success = false, message = "无效请求" });
                accessControlService.SetConnectionLimitConfig(
                    request.MaxConnectionsPerIp,
                    request.MaxConnectionsPerDestination,
                    request.MaxTotalConnections,
                    request.RejectStatusCode);
                return Results.Json(new { success = true, message = "连接限制配置已更新" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加路径连接限制
        app.MapPost("/api/connection-limit/path/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<PathConnectionLimitRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.MaxConnections <= 0)
                    return Results.Json(new { success = false, message = "无效参数" });
                accessControlService.SetPathConnectionLimit(request.Path, request.MaxConnections);
                return Results.Json(new { success = true, message = $"已设置路径 {request.Path} 最大连接数: {request.MaxConnections}" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除路径连接限制
        app.MapPost("/api/connection-limit/path/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemovePathConnectionLimitRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                    return Results.Json(new { success = false, message = "无效参数" });
                if (accessControlService.RemovePathConnectionLimit(request.Path))
                    return Results.Json(new { success = true, message = $"已移除路径 {request.Path} 连接限制" });
                return Results.Json(new { success = false, message = "路径不在限制列表中" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Args 检测状态
        app.MapPost("/api/feature/waf-args/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenArgsCheck;
                protectService.SetArgsCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-args",
                    enabled = enabled,
                    message = enabled ? "WAF Args 检测已启用" : "WAF Args 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 切换 WAF Post 检测状态
        app.MapPost("/api/feature/waf-post/toggle", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleFeatureRequest>();
                var enabled = request?.Enabled ?? !protectService.GetOptions().OpenPostCheck;
                protectService.SetPostCheckEnabled(enabled);
                return Results.Json(new
                {
                    success = true,
                    feature = "waf-post",
                    enabled = enabled,
                    message = enabled ? "WAF Post 检测已启用" : "WAF Post 检测已禁用",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 动态访问控制管理 API ===============

        // 获取所有访问控制数据（黑白名单页面用）
        app.MapGet("/api/ac/all", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var whitelist = accessControlService.GetWhitelist();
            var blacklist = accessControlService.GetBlacklist();
            var denyCountries = accessControlService.GetDenyCountries();
            var denyRegions = accessControlService.GetDenyRegions();
            var allowCountries = accessControlService.GetAllowCountries();
            var allowRegions = accessControlService.GetAllowRegions();
            var secSnapshot = SharedData.Security.GetSnapshot();

            return Results.Json(new
            {
                success = true,
                ipControl = new
                {
                    enabled = options.IpControl.Enabled,
                    whitelist,
                    blacklist,
                },
                geoControl = new
                {
                    enabled = options.GeoControl.Enabled,
                    mode = options.GeoControl.Mode.ToString(),
                    denyCountries,
                    denyRegions,
                    allowCountries,
                    allowRegions,
                },
                stats = new
                {
                    blacklistBlockCount = secSnapshot.BlacklistBlockCount,
                    geoBlockCount = secSnapshot.GeoBlockCount,
                },
                rejectStatusCode = options.RejectStatusCode,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 切换 IP 访问控制启用状态
        app.MapPost("/api/ac/ip-control/toggle", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var newState = !options.IpControl.Enabled;
            accessControlService.SetIpControlEnabled(newState);
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"IP 访问控制: {(newState ? "启用" : "禁用")}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, enabled = newState, message = newState ? "IP 访问控制已启用" : "IP 访问控制已禁用" });
        }).RequireHost($"*:{controlPort}");

        // 切换地理位置访问控制启用状态
        app.MapPost("/api/ac/geo-control/toggle", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var options = accessControlService.GetOptions();
            var newState = !options.GeoControl.Enabled;
            accessControlService.SetGeoControlEnabled(newState);
            var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"地理位置访问控制: {(newState ? "启用" : "禁用")}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new { success = true, enabled = newState, message = newState ? "地理位置访问控制已启用" : "地理位置访问控制已禁用" });
        }).RequireHost($"*:{controlPort}");

        // 获取白名单列表
        app.MapGet("/api/ac/whitelist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var whitelist = accessControlService.GetWhitelist();
            return Results.Json(new
            {
                success = true,
                count = whitelist.Count,
                whitelist,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到白名单
        app.MapPost("/api/ac/whitelist/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.AddWhitelist(request.IpOrCidr);
                if (result)
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"添加白名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功添加白名单: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从白名单移除 IP
        app.MapPost("/api/ac/whitelist/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.RemoveWhitelist(request.IpOrCidr);
                if (result)
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"移除白名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功从白名单移除: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在动态白名单中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取黑名单列表
        app.MapGet("/api/ac/blacklist", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var blacklist = accessControlService.GetBlacklist();
            return Results.Json(new
            {
                success = true,
                count = blacklist.Count,
                blacklist = blacklist,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到黑名单
        app.MapPost("/api/ac/blacklist/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.AddBlacklist(request.IpOrCidr);
                if (result)
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"添加黑名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功添加黑名单: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：IP 格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从黑名单移除 IP
        app.MapPost("/api/ac/blacklist/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.IpOrCidr))
                {
                    return Results.Json(new { success = false, message = "IP 或 CIDR 不能为空" }, statusCode: 400);
                }

                var result = accessControlService.RemoveBlacklist(request.IpOrCidr);
                if (result)
                {
                    var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                    audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown", $"移除黑名单: {request.IpOrCidr}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已成功从黑名单移除: {request.IpOrCidr}",
                        ipOrCidr = request.IpOrCidr,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在动态黑名单中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 被封禁 IP 管理 API ===============
        
        // 获取被封禁的 IP 列表
        app.MapGet("/api/blocked-ips", (HttpContext ctx) =>
        {
            var blockedIps = SharedData.ClientFb.GetValidItemsWithExpiry()
                .Select(x => new
                {
                    ip = x.Key,
                    type = "blocked",
                    reason = x.Value,
                    remainingSeconds = x.RemainingTime?.TotalSeconds,
                    expiresAt = SharedData.ClientFb.GetExpiration(x.Key)
                })
                .Concat(SharedData.CaptchaPending.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "captcha",
                        reason = $"验证码待验证: {x.Value.RuleName}",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.CaptchaPending.GetExpiration(x.Key)
                    }))
                .Concat(SharedData.ClientThrottled.GetValidItemsWithExpiry()
                    .Select(x => new
                    {
                        ip = x.Key,
                        type = "throttled",
                        reason = $"带宽限速: {x.Value.EveryCapacity / 1024}KB/s",
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        expiresAt = SharedData.ClientThrottled.GetExpiration(x.Key)
                    }))
                .Concat(SharedData.IpLogTargets.GetValidItemsWithExpiry()
                    .Select(x =>
                    {
                        var (exists, sizeBytes, _) = IpRequestLogger.GetLogFileInfo(x.Key);
                        return new
                        {
                            ip = x.Key,
                            type = "log",
                            reason = exists ? $"请求日志记录中 ({sizeBytes / 1024}KB)" : "请求日志记录中",
                            remainingSeconds = x.RemainingTime?.TotalSeconds,
                            expiresAt = SharedData.IpLogTargets.GetExpiration(x.Key)
                        };
                    }))
                .OrderBy(x => x.ip)
                .ToList();

            return Results.Json(new
            {
                success = true,
                count = blockedIps.Count,
                blockedIps = blockedIps,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 手动添加 IP 限制（支持 blocked/captcha/throttled/log 类型）
        app.MapPost("/api/blocked-ips/add", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<BlockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var duration = request.Duration ?? TimeSpan.FromMinutes(10);
                var reason = request.Reason ?? "手动封禁";
                var type = request.Type ?? "blocked";
                string message;

                switch (type)
                {
                    case "captcha":
                        var captchaInfo = new CaptchaPendingInfo
                        {
                            RuleName = reason.Length > 0 ? reason : "手动验证码",
                            ActionSeconds = (int)duration.TotalSeconds,
                            CreatedAt = DateTime.UtcNow
                        };
                        SharedData.CaptchaPending.Set(ip, captchaInfo, duration);
                        message = $"已对 IP 启用验证码: {ip}";
                        break;

                    case "throttled":
                        var speedKBps = request.SpeedLimit > 0 ? request.SpeedLimit : 100;
                        var throttleLimit = new ClientThrottledLimit
                        {
                            EveryCapacity = speedKBps * 1024,
                            LeftToken = speedKBps * 1024,
                        };
                        SharedData.ClientThrottled.Set(ip, throttleLimit, duration);
                        message = $"已对 IP 启用限速: {ip} ({speedKBps}KB/s)";
                        break;

                    case "log":
                        SharedData.IpLogTargets.Set(ip, DateTime.Now, duration);
                        message = $"已开始记录 IP: {ip} 的请求日志 ({duration.TotalMinutes:0}分钟)";
                        break;

                    default: // "blocked"
                        SharedData.ClientFb.Set(ip, reason, duration);
                        message = $"已封禁 IP: {ip}";
                        break;
                }

                return Results.Json(new
                {
                    success = true,
                    message = message,
                    ip = ip,
                    type = type,
                    reason = reason,
                    duration = duration.ToString(),
                    expiresAt = DateTime.Now.Add(duration),
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 解封 IP
        app.MapPost("/api/blocked-ips/remove", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UnblockIpRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var removed = SharedData.ClientFb.Remove(ip);
                removed |= SharedData.CaptchaPending.Remove(ip);
                removed |= SharedData.ClientThrottled.Remove(ip);
                removed |= SharedData.IpLogTargets.Remove(ip);

                if (removed)
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已解封 IP: {ip}",
                        ip = ip,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "IP 不在封禁列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"解封失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 清空所有封禁的 IP
        app.MapPost("/api/blocked-ips/clear", (HttpContext ctx) =>
        {
            var count = SharedData.ClientFb.Count + SharedData.CaptchaPending.Count + SharedData.ClientThrottled.Count + SharedData.IpLogTargets.Count;
            SharedData.ClientFb.Clear();
            SharedData.CaptchaPending.Clear();
            SharedData.ClientThrottled.Clear();
            SharedData.IpLogTargets.Clear();
            return Results.Json(new
            {
                success = true,
                message = $"已清空 {count} 个被封禁的 IP",
                clearedCount = count,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== IP 请求日志 API ===============

        // 获取当前正在监控的 IP 列表
        app.MapGet("/api/ip-log/list", (HttpContext ctx) =>
        {
            var targets = SharedData.IpLogTargets.GetValidItemsWithExpiry()
                .Select(x =>
                {
                    var (exists, sizeBytes, lastWriteTime) = IpRequestLogger.GetLogFileInfo(x.Key);
                    return new
                    {
                        ip = x.Key,
                        addedAt = x.Value,
                        remainingSeconds = x.RemainingTime?.TotalSeconds,
                        logFileExists = exists,
                        logFileSize = sizeBytes,
                        lastWriteTime = lastWriteTime,
                    };
                })
                .OrderBy(x => x.ip)
                .ToList();

            return Results.Json(new
            {
                success = true,
                count = targets.Count,
                targets = targets,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 IP 到请求日志监控
        app.MapPost("/api/ip-log/add", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var duration = request.Duration ?? TimeSpan.FromMinutes(10);
                SharedData.IpLogTargets.Set(ip, DateTime.Now, duration);

                return Results.Json(new
                {
                    success = true,
                    message = $"已开始记录 IP: {ip} 的请求日志",
                    ip = ip,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 停止 IP 请求日志监控
        app.MapPost("/api/ip-log/remove", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var removed = SharedData.IpLogTargets.Remove(ip);

                return Results.Json(new
                {
                    success = removed,
                    message = removed ? $"已停止记录 IP: {ip}" : "IP 不在监控列表中",
                    ip = ip,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 读取 IP 的请求日志（分页，按条目解析）
        app.MapGet("/api/ip-log/read/{ip}", async (HttpContext ctx, string ip, int offset = 0, int limit = 50) =>
        {
            var (exists, sizeBytes, lastWriteTime) = IpRequestLogger.GetLogFileInfo(ip);
            if (!exists)
            {
                return Results.Json(new
                {
                    success = true,
                    ip = ip,
                    entries = Array.Empty<object>(),
                    total = 0,
                    fileSize = 0L,
                    timestamp = DateTime.Now
                });
            }

            var entries = await IpRequestLogger.ReadEntriesAsync(ip, offset, limit);
            var total = await IpRequestLogger.CountEntriesAsync(ip);

            return Results.Json(new
            {
                success = true,
                ip = ip,
                entries = entries,
                total = total,
                offset = offset,
                limit = limit,
                fileSize = sizeBytes,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 删除 IP 的日志文件
        app.MapPost("/api/ip-log/delete-file", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<IpLogRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                var ip = request.Ip.Trim();
                var deleted = IpRequestLogger.DeleteLog(ip);

                return Results.Json(new
                {
                    success = deleted,
                    message = deleted ? $"已删除 {ip} 的日志文件" : "日志文件不存在",
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== WAF 规则管理 API ===============

        // 获取 WAF 规则列表
        app.MapGet("/api/waf/rules", (HttpContext ctx, IProtectService protectService) =>
        {
            var options = protectService.GetOptions();
            return Results.Json(new
            {
                success = true,
                openArgsCheck = options.OpenArgsCheck,
                openPostCheck = options.OpenPostCheck,
                argsRules = protectService.GetArgsRegexList(),
                postRules = protectService.GetPostRegexList(),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Args 规则
        app.MapPost("/api/waf/args/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Args 规则
        app.MapPost("/api/waf/args/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemoveArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Post 规则
        app.MapPost("/api/waf/post/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddPostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Post 规则
        app.MapPost("/api/waf/post/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemovePostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 统计配置管理 API ===============

        // 获取统计配置（白名单路径 + 路径统计规则）
        app.MapGet("/api/statistic/config", (HttpContext ctx, IStatisticService statisticService) =>
        {
            return Results.Json(new
            {
                success = true,
                whitePaths = statisticService.GetWhitePaths(),
                pathStas = statisticService.GetPathStas(),
            });
        }).RequireHost($"*:{controlPort}");

        // 添加白名单路径
        app.MapPost("/api/statistic/white-paths/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<PathRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.AddWhitePath(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"添加白名单路径: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "添加成功" });
            }
            return Results.Json(new { success = false, message = "路径已存在" });
        }).RequireHost($"*:{controlPort}");

        // 移除白名单路径
        app.MapPost("/api/statistic/white-paths/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<PathRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.RemoveWhitePath(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"移除白名单路径: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "移除成功" });
            }
            return Results.Json(new { success = false, message = "路径不存在" });
        }).RequireHost($"*:{controlPort}");

        // 添加路径统计规则
        app.MapPost("/api/statistic/path-stas/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<PathRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.AddPathSta(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"添加路径统计规则: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "添加成功" });
            }
            return Results.Json(new { success = false, message = "路径已存在" });
        }).RequireHost($"*:{controlPort}");

        // 移除路径统计规则
        app.MapPost("/api/statistic/path-stas/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            var request = await ctx.Request.ReadFromJsonAsync<PathRequest>();
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.RemovePathSta(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"移除路径统计规则: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "移除成功" });
            }
            return Results.Json(new { success = false, message = "路径不存在" });
        }).RequireHost($"*:{controlPort}");

        // =============== CC 防护规则管理 API ===============

        // 获取 CC 规则列表
        app.MapGet("/api/cc/rules", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var options = statisticService.GetOption();
            return Results.Json(new
            {
                success = true,
                rules = statisticService.GetLimitCcRules().Select(r => new
                {
                    path = r.Path,
                    period = r.Period,
                    limitNum = r.LimitNum,
                    fbTime = r.FbTime.ToString()
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 CC 规则
        app.MapPost("/api/cc/rules/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                var rule = new LimitCcOption
                {
                    Path = request.Path,
                    Period = request.Period ?? 60,
                    LimitNum = request.LimitNum ?? 100,
                    FbTime = request.FbTime ?? TimeSpan.FromMinutes(5)
                };

                if (statisticService.AddLimitCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 CC 规则: {request.Path}",
                        rule = new { rule.Path, rule.Period, rule.LimitNum, fbTime = rule.FbTime.ToString() },
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：规则已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 CC 规则
        app.MapPost("/api/cc/rules/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (statisticService.RemoveLimitCcRule(request.Path))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 CC 规则: {request.Path}",
                        path = request.Path,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 高级 CC 规则管理 API ===============
        
        // 获取所有高级 CC 规则
        app.MapGet("/api/cc/advanced", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var rules = statisticService.GetAdvancedCcRules();
            return Results.Json(new
            {
                success = true,
                count = rules.Count,
                rules = rules.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    enabled = r.Enabled,
                    type = r.Type.ToString(),
                    conditions = r.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = r.Period,
                    threshold = r.Threshold,
                    action = r.Action.ToString(),
                    actionSeconds = r.ActionSeconds,
                    priority = r.Priority,
                    createdAt = r.CreatedAt
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 按类型获取高级 CC 规则
        app.MapGet("/api/cc/advanced/type/{type}", (HttpContext ctx, IStatisticService statisticService, string type) =>
        {
            if (!Enum.TryParse<CcRuleType>(type, true, out var ruleType))
            {
                return Results.Json(new { success = false, message = "无效的规则类型" }, statusCode: 400);
            }
            
            var rules = statisticService.GetAdvancedCcRulesByType(ruleType);
            return Results.Json(new
            {
                success = true,
                type = ruleType.ToString(),
                count = rules.Count,
                rules = rules.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    enabled = r.Enabled,
                    type = r.Type.ToString(),
                    conditions = r.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = r.Period,
                    threshold = r.Threshold,
                    action = r.Action.ToString(),
                    actionSeconds = r.ActionSeconds,
                    priority = r.Priority,
                    createdAt = r.CreatedAt
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单个高级 CC 规则
        app.MapGet("/api/cc/advanced/{ruleId}", (HttpContext ctx, IStatisticService statisticService, string ruleId) =>
        {
            var rule = statisticService.GetAdvancedCcRule(ruleId);
            if (rule == null)
            {
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            }
            
            return Results.Json(new
            {
                success = true,
                rule = new
                {
                    id = rule.Id,
                    name = rule.Name,
                    enabled = rule.Enabled,
                    type = rule.Type.ToString(),
                    conditions = rule.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = rule.Period,
                    threshold = rule.Threshold,
                    action = rule.Action.ToString(),
                    actionSeconds = rule.ActionSeconds,
                    priority = rule.Priority,
                    createdAt = rule.CreatedAt
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加高级 CC 规则
        app.MapPost("/api/cc/advanced/add", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.Json(new { success = false, message = "规则名称不能为空" }, statusCode: 400);
                }

                var rule = new AdvancedCcRule
                {
                    Name = request.Name,
                    Enabled = request.Enabled ?? true,
                    Type = Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : CcRuleType.FrequentAccess,
                    Period = request.Period ?? 10,
                    Threshold = request.Threshold ?? 100,
                    Action = Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : CcAction.Captcha,
                    ActionSeconds = request.ActionSeconds ?? 600,
                    Priority = request.Priority ?? 100,
                    Conditions = request.Conditions?.Select(c => new CcCondition
                    {
                        Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                        Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                        Values = c.Values ?? []
                    }).ToList() ?? []
                };

                if (statisticService.AddAdvancedCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加高级 CC 规则: {rule.Name}",
                        ruleId = rule.Id,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新高级 CC 规则
        app.MapPost("/api/cc/advanced/update", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Id))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                var existingRule = statisticService.GetAdvancedCcRule(request.Id);
                if (existingRule == null)
                {
                    return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
                }

                var rule = new AdvancedCcRule
                {
                    Id = request.Id,
                    Name = request.Name ?? existingRule.Name,
                    Enabled = request.Enabled ?? existingRule.Enabled,
                    Type = request.Type != null && Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : existingRule.Type,
                    Period = request.Period ?? existingRule.Period,
                    Threshold = request.Threshold ?? existingRule.Threshold,
                    Action = request.Action != null && Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : existingRule.Action,
                    ActionSeconds = request.ActionSeconds ?? existingRule.ActionSeconds,
                    Priority = request.Priority ?? existingRule.Priority,
                    Conditions = request.Conditions?.Select(c => new CcCondition
                    {
                        Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                        Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                        Values = c.Values ?? []
                    }).ToList() ?? existingRule.Conditions
                };

                if (statisticService.UpdateAdvancedCcRule(rule))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已更新高级 CC 规则: {rule.Name}",
                        ruleId = rule.Id,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "更新失败" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除高级 CC 规则
        app.MapPost("/api/cc/advanced/remove", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                if (statisticService.RemoveAdvancedCcRule(request.RuleId))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = "已删除高级 CC 规则",
                        ruleId = request.RuleId,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "删除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 启用/禁用高级 CC 规则
        app.MapPost("/api/cc/advanced/toggle", async (HttpContext ctx, IStatisticService statisticService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<ToggleAdvancedCcRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
                {
                    return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
                }

                var rule = statisticService.GetAdvancedCcRule(request.RuleId);
                var newState = request.Enabled ?? !(rule?.Enabled ?? false);

                if (statisticService.ToggleAdvancedCcRule(request.RuleId, newState))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = newState ? "已启用规则" : "已禁用规则",
                        ruleId = request.RuleId,
                        enabled = newState,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "操作失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取可用的匹配目标、操作符、动作等枚举值
        app.MapGet("/api/cc/advanced/enums", (HttpContext ctx) =>
        {
            return Results.Json(new
            {
                success = true,
                matchTargets = Enum.GetNames<CcMatchTarget>().Select(n => new { name = n, value = n }),
                matchOperators = Enum.GetNames<CcMatchOperator>().Select(n => new { name = n, value = n }),
                ruleTypes = Enum.GetNames<CcRuleType>().Select(n => new { name = n, value = n }),
                actions = Enum.GetNames<CcAction>().Select(n => new { name = n, value = n }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== 地理位置黑名单管理 API ===============
        
        // 获取地理位置黑名单
        app.MapGet("/api/geo/deny-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetDenyCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                denyCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到地理位置黑名单
        app.MapPost("/api/geo/deny-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从地理位置黑名单移除国家
        app.MapPost("/api/geo/deny-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取禁止访问的省份列表
        app.MapGet("/api/geo/deny-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetDenyRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                denyRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到禁止列表
        app.MapPost("/api/geo/deny-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加禁止访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从禁止列表移除省份
        app.MapPost("/api/geo/deny-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveDenyRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从禁止列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态黑名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的国家列表
        app.MapGet("/api/geo/allow-countries", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var countries = accessControlService.GetAllowCountries();
            return Results.Json(new
            {
                success = true,
                count = countries.Count,
                allowCountries = countries,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加国家到允许列表
        app.MapPost("/api/geo/allow-countries/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除国家
        app.MapPost("/api/geo/allow-countries/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveCountryRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Country))
                {
                    return Results.Json(new { success = false, message = "国家/地区名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowCountry(request.Country))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除国家/地区: {request.Country}",
                        country = request.Country,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取允许访问的省份列表
        app.MapGet("/api/geo/allow-regions", (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            var regions = accessControlService.GetAllowRegions();
            return Results.Json(new
            {
                success = true,
                count = regions.Count,
                allowRegions = regions,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加省份到允许列表
        app.MapPost("/api/geo/allow-regions/add", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.AddAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加允许访问省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：已存在或参数无效" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 从允许列表移除省份
        app.MapPost("/api/geo/allow-regions/remove", async (HttpContext ctx, IAccessControlService accessControlService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRegionRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Region))
                {
                    return Results.Json(new { success = false, message = "省份名称不能为空" }, statusCode: 400);
                }

                if (accessControlService.RemoveAllowRegion(request.Region))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已从允许列表移除省份: {request.Region}",
                        region = request.Region,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：不存在于动态白名单" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 带宽限速管理 API ===============

        // 获取带宽限速配置
        app.MapGet("/api/throttle/config", (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            var config = speedLimitService.GetThrottleConfig();
            var options = speedLimitService.GetOptions();
            return Results.Json(new
            {
                success = true,
                enabled = options.Throttled.Enabled,
                global = config.Global,
                pathLimits = config.PathLimits,
                ipLimits = config.IpLimits,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 切换带宽限速启用状态
        app.MapPost("/api/throttle/toggle", (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            var options = speedLimitService.GetOptions();
            options.Throttled.Enabled = !options.Throttled.Enabled;
            return Results.Json(new
            {
                success = true,
                enabled = options.Throttled.Enabled,
                message = options.Throttled.Enabled ? "带宽限速已启用" : "带宽限速已禁用"
            });
        }).RequireHost($"*:{controlPort}");

        // 设置全局带宽限速
        app.MapPost("/api/throttle/global", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<SetGlobalThrottleRequest>();
                if (request == null)
                    return Results.Json(new { success = false, message = "请求无效" }, statusCode: 400);

                speedLimitService.SetGlobalThrottle(request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = request.LimitKbps > 0
                        ? $"已设置全局带宽限速: {request.LimitKbps} KB/s"
                        : "已关闭全局带宽限速",
                    global = request.LimitKbps
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 设置 IP 带宽限速
        app.MapPost("/api/throttle/ip/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetIpThrottle(request.Ip, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置 IP {request.Ip} 带宽限速: {request.LimitKbps} KB/s",
                    ip = request.Ip,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 IP 带宽限速
        app.MapPost("/api/throttle/ip/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveIpThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Ip))
                {
                    return Results.Json(new { success = false, message = "IP 不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemoveIpThrottle(request.Ip))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 IP {request.Ip} 的带宽限速",
                        ip = request.Ip,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：IP 不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 设置路径带宽限速
        app.MapPost("/api/throttle/path/add", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddPathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (request.LimitKbps <= 0)
                {
                    return Results.Json(new { success = false, message = "限速值必须大于 0" }, statusCode: 400);
                }

                speedLimitService.SetPathThrottle(request.Path, request.LimitKbps);
                return Results.Json(new
                {
                    success = true,
                    message = $"已设置路径 {request.Path} 带宽限速: {request.LimitKbps} KB/s",
                    path = request.Path,
                    limitKbps = request.LimitKbps,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"设置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除路径带宽限速
        app.MapPost("/api/throttle/path/remove", async (HttpContext ctx, ISpeedLimitService speedLimitService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemovePathThrottleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
                }

                if (speedLimitService.RemovePathThrottle(request.Path))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除路径 {request.Path} 的带宽限速",
                        path = request.Path,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：路径不在限速列表中" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存当前限速配置到补丁
        app.MapPost("/api/throttle/patch/save", async (HttpContext ctx, ISpeedLimitService speedLimitService, IConfiguration config) =>
        {
            try
            {
                var throttleConfig = speedLimitService.GetThrottleConfig();
                var options = speedLimitService.GetOptions();
                await _lbPatchLock.WaitAsync();
                try
                {
                    var patch = await ReadPatchFile();
                    var speedLimit = EnsurePatchSection(patch, "SpeedLimit");
                    var throttled = new Dictionary<string, object>
                    {
                        ["Enabled"] = options.Throttled.Enabled,
                        ["Global"] = throttleConfig.Global,
                        ["Everys"] = throttleConfig.PathLimits.ToDictionary(k => k.Key, k => (object)k.Value),
                        ["IpEverys"] = throttleConfig.IpLimits.ToDictionary(k => k.Key, k => (object)k.Value)
                    };
                    speedLimit["Throttled"] = throttled;
                    UpdatePatchSourceTracking(patch);
                    await SavePatchAndReload(patch, config);
                    return Results.Json(new { success = true, message = "限速配置已保存到补丁" });
                }
                finally
                {
                    _lbPatchLock.Release();
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除补丁中的限速配置（恢复为原始配置）
        app.MapPost("/api/throttle/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                await _lbPatchLock.WaitAsync();
                try
                {
                    var patch = await ReadPatchFile();
                    if (patch.Remove("SpeedLimit"))
                    {
                        UpdatePatchSourceTracking(patch);
                        await SavePatchAndReload(patch, config);
                        return Results.Json(new { success = true, message = "已删除补丁中的限速配置，恢复为原始配置" });
                    }
                    return Results.Json(new { success = false, message = "补丁中没有限速配置" }, statusCode: 404);
                }
                finally
                {
                    _lbPatchLock.Release();
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== A/B 测试管理 API ===============

        // 获取所有 A/B 测试配置
        app.MapGet("/api/abtest/configs", (HttpContext ctx, IABTestService abTestService) =>
        {
            var configs = abTestService.GetAllConfigs();
            return Results.Json(new
            {
                success = true,
                count = configs.Count,
                configs = configs.Select(c => new
                {
                    testId = c.Key,
                    name = c.Value.Name,
                    enabled = c.Value.Enabled,
                    mode = c.Value.Mode.ToString(),
                    cookieName = c.Value.CookieName,
                    variants = c.Value.Variants,
                    variantTargets = c.Value.VariantTargets,
                    matchPaths = c.Value.MatchPaths,
                    excludePaths = c.Value.ExcludePaths
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单个 A/B 测试配置
        app.MapGet("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            return Results.Json(new
            {
                success = true,
                testId = testId,
                config = new
                {
                    name = config.Name,
                    enabled = config.Enabled,
                    mode = config.Mode.ToString(),
                    cookieName = config.CookieName,
                    cookieExpireDays = config.CookieExpireDays,
                    variants = config.Variants,
                    variantTargets = config.VariantTargets,
                    matchPaths = config.MatchPaths,
                    excludePaths = config.ExcludePaths
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 创建或更新 A/B 测试配置
        app.MapPost("/api/abtest/configs", async (HttpContext ctx, IABTestService abTestService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CreateABTestRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.TestId))
                {
                    return Results.Json(new { success = false, message = "测试 ID 不能为空" }, statusCode: 400);
                }

                if (request.Variants == null || request.Variants.Count < 2)
                {
                    return Results.Json(new { success = false, message = "至少需要 2 个变体" }, statusCode: 400);
                }

                // 验证权重总和
                var totalWeight = request.Variants.Values.Sum();
                if (totalWeight <= 0)
                {
                    return Results.Json(new { success = false, message = "权重总和必须大于 0" }, statusCode: 400);
                }

                var config = new ABTestConfig
                {
                    Name = request.Name ?? request.TestId,
                    Enabled = request.Enabled ?? true,
                    Mode = Enum.TryParse<ABTestMode>(request.Mode, true, out var mode) ? mode : ABTestMode.CookieSticky,
                    CookieName = request.CookieName ?? $"ab_{request.TestId}",
                    CookieExpireDays = request.CookieExpireDays ?? 30,
                    Variants = request.Variants,
                    VariantTargets = request.VariantTargets ?? new Dictionary<string, string>(),
                    MatchPaths = request.MatchPaths ?? new List<string>(),
                    ExcludePaths = request.ExcludePaths ?? new List<string>()
                };

                abTestService.SetConfig(request.TestId, config);

                return Results.Json(new
                {
                    success = true,
                    message = $"已创建/更新 A/B 测试: {request.TestId}",
                    testId = request.TestId,
                    config = new
                    {
                        name = config.Name,
                        enabled = config.Enabled,
                        mode = config.Mode.ToString(),
                        variants = config.Variants,
                        totalWeight = totalWeight
                    },
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"创建失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除 A/B 测试配置
        app.MapDelete("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            if (abTestService.RemoveConfig(testId))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已删除 A/B 测试: {testId}",
                    testId = testId,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }
        }).RequireHost($"*:{controlPort}");

        // 启用/禁用 A/B 测试
        app.MapPost("/api/abtest/configs/{testId}/toggle", async (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            var request = await ctx.Request.ReadFromJsonAsync<ToggleABTestRequest>();
            config.Enabled = request?.Enabled ?? !config.Enabled;
            abTestService.SetConfig(testId, config);

            return Results.Json(new
            {
                success = true,
                message = config.Enabled ? "已启用 A/B 测试" : "已禁用 A/B 测试",
                testId = testId,
                enabled = config.Enabled,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取 A/B 测试统计
        app.MapGet("/api/abtest/stats/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var stats = abTestService.GetStats(testId);
            var config = abTestService.GetConfig(testId);
            
            if (stats == null || config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            // 计算各变体的实际百分比
            var variantPercentages = stats.VariantHits.ToDictionary(
                v => v.Key,
                v => stats.TotalRequests > 0 ? Math.Round((double)v.Value / stats.TotalRequests * 100, 2) : 0
            );

            return Results.Json(new
            {
                success = true,
                testId = testId,
                stats = new
                {
                    totalRequests = stats.TotalRequests,
                    variantHits = stats.VariantHits,
                    variantPercentages = variantPercentages,
                    configuredWeights = config.Variants,
                    startTime = stats.StartTime,
                    lastRequestTime = stats.LastRequestTime
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 重置 A/B 测试统计
        app.MapPost("/api/abtest/stats/{testId}/reset", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            abTestService.ResetStats(testId);

            return Results.Json(new
            {
                success = true,
                message = "已重置 A/B 测试统计",
                testId = testId,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // =============== 负载均衡管理 API ===============

        // 获取所有集群及其目标列表
        app.MapGet("/api/lb/clusters", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var rpSection = config.GetSection("ReverseProxy:Clusters");
                var clusters = new List<object>();
                foreach (var clusterSection in rpSection.GetChildren())
                {
                    var clusterId = clusterSection.Key;
                    var policy = clusterSection["LoadBalancingPolicy"] ?? "RoundRobin";
                    var destinations = new List<object>();
                    var destSection = clusterSection.GetSection("Destinations");
                    foreach (var dest in destSection.GetChildren())
                    {
                        var address = dest["Address"] ?? "";
                        Uri.TryCreate(address, UriKind.Absolute, out var uri);
                        var metadata = new Dictionary<string, string>();
                        var metaSection = dest.GetSection("Metadata");
                        foreach (var meta in metaSection.GetChildren())
                        {
                            metadata[meta.Key] = meta.Value ?? "";
                        }
                        var destSource = DetectConfigSource(config, $"ReverseProxy:Clusters:{clusterId}:Destinations:{dest.Key}");
                        destinations.Add(new
                        {
                            id = dest.Key,
                            address,
                            host = uri?.Host ?? "*",
                            port = uri?.Port ?? 0,
                            scheme = uri?.Scheme ?? "http",
                            metadata,
                            source = destSource == ConfigSource.PatchConfig ? "patch" : "original"
                        });
                    }
                    clusters.Add(new
                    {
                        id = clusterId,
                        loadBalancingPolicy = policy,
                        destinations,
                        destinationCount = destinations.Count
                    });
                }
                return Results.Json(new { success = true, clusters, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取集群列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取所有可用负载均衡策略
        app.MapGet("/api/lb/policies", (HttpContext ctx) =>
        {
            var policies = new[]
            {
                new { name = "RoundRobin", label = "轮询" },
                new { name = "Random", label = "随机" },
                new { name = "LeastRequests", label = "最少连接" },
                new { name = "PowerOfTwoChoices", label = "二选一" },
                new { name = "First", label = "总是第一个" },
                new { name = "WeightedRoundRobin", label = "加权轮询" },
                new { name = "WeightedLeastConnections", label = "加权最少连接" },
                new { name = "IpHash", label = "IP 哈希" },
                new { name = "GenericHash", label = "通用哈希" },
                new { name = "WeightedRandom", label = "加权随机" },
                new { name = "ConsistentHash", label = "一致性哈希" },
            };
            return Results.Json(new { success = true, policies });
        }).RequireHost($"*:{controlPort}");

        // 更新集群的负载均衡策略
        app.MapPost("/api/lb/policy/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateClusterPolicyRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.Policy))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 Policy 不能为空" }, statusCode: 400);
                }

                var clusterSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}");
                if (!clusterSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"集群 {request.ClusterId} 不存在" }, statusCode: 400);
                }

                // 检测策略配置的来源
                var policyConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:LoadBalancingPolicy";
                var configSource = DetectConfigSource(config, policyConfigKey);

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        clusterDict["LoadBalancingPolicy"] = request.Policy;
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        cluster["LoadBalancingPolicy"] = request.Policy;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新策略失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加目标服务器
        app.MapPost("/api/lb/destinations/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) ||
                    string.IsNullOrEmpty(request.DestinationId) || string.IsNullOrEmpty(request.Address))
                {
                    return Results.Json(new { success = false, message = "ClusterId、DestinationId 和 Address 不能为空" }, statusCode: 400);
                }

                // 检查集群是否存在
                var clusterSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}");
                if (!clusterSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"集群 {request.ClusterId} 不存在" }, statusCode: 400);
                }

                // 检查目标是否已存在
                var existDest = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}");
                if (existDest.Exists())
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 已存在" }, statusCode: 400);
                }

                // 检测集群配置的来源（Destinations 或集群本身）
                var clusterConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}";
                var destinationsConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations";
                var configSource = DetectConfigSource(config, destinationsConfigKey);
                
                if (configSource == ConfigSource.NotFound)
                {
                    configSource = DetectConfigSource(config, clusterConfigKey);
                }

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        // 确保Destinations存在
                        if (!clusterDict.TryGetValue("Destinations", out var destsObj) || destsObj is not Dictionary<string, object> destsDict)
                        {
                            destsDict = new Dictionary<string, object>();
                            clusterDict["Destinations"] = destsDict;
                        }
                        
                        // 添加新的目标
                        var dest = new Dictionary<string, object> { ["Address"] = request.Address };
                        if (request.Metadata != null && request.Metadata.Count > 0)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        destsDict[request.DestinationId] = dest;
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件（只写入新增的目标，不快照整个集群）
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        var dests = EnsurePatchDestinations(cluster);
                        var dest = new Dictionary<string, object> { ["Address"] = request.Address };
                        if (request.Metadata != null && request.Metadata.Count > 0)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        dests[request.DestinationId] = dest;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 编辑目标服务器
        app.MapPost("/api/lb/destinations/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.DestinationId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationId 不能为空" }, statusCode: 400);
                }

                var destSection = config.GetSection($"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}");
                if (!destSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 不存在" }, statusCode: 400);
                }

                // 检测目标配置的来源
                var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}";
                var configSource = DetectConfigSource(config, destConfigKey);

                if (configSource == ConfigSource.OriginalConfig)
                {
                    // 修改原始配置文件
                    return await ModifyOriginalConfig(config, request.ClusterId, configObj =>
                    {
                        if (configObj is not Dictionary<string, object> rootDict)
                            return "配置文件格式错误";
                            
                        if (!rootDict.TryGetValue("ReverseProxy", out var rpObj) || rpObj is not Dictionary<string, object> rpDict)
                            return "ReverseProxy 配置不存在";
                            
                        if (!rpDict.TryGetValue("Clusters", out var clustersObj) || clustersObj is not Dictionary<string, object> clustersDict)
                            return "Clusters 配置不存在";
                            
                        if (!clustersDict.TryGetValue(request.ClusterId, out var clusterObj) || clusterObj is not Dictionary<string, object> clusterDict)
                            return $"集群 {request.ClusterId} 不存在";
                            
                        if (!clusterDict.TryGetValue("Destinations", out var destsObj) || destsObj is not Dictionary<string, object> destsDict)
                            return "Destinations 配置不存在";
                            
                        if (!destsDict.TryGetValue(request.DestinationId, out var destObj) || destObj is not Dictionary<string, object> destDict)
                            return $"目标 {request.DestinationId} 不存在";
                            
                        // 更新目标配置
                        if (!string.IsNullOrEmpty(request.Address)) destDict["Address"] = request.Address;
                        if (request.Metadata != null)
                        {
                            destDict["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        return null;
                    });
                }
                else
                {
                    // 修改补丁配置文件（只写入被修改的目标）
                    return await ModifyLbPatch(config, patch =>
                    {
                        var cluster = EnsurePatchCluster(patch, request.ClusterId);
                        var dests = EnsurePatchDestinations(cluster);

                        var dest = dests.TryGetValue(request.DestinationId, out var existObj) && existObj is Dictionary<string, object> existDict
                            ? existDict : new Dictionary<string, object>();

                        if (!string.IsNullOrEmpty(request.Address)) dest["Address"] = request.Address;
                        if (request.Metadata != null)
                        {
                            dest["Metadata"] = request.Metadata.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        }
                        dests[request.DestinationId] = dest;
                        return null;
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除目标服务器
        app.MapPost("/api/lb/destinations/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveDestinationRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || string.IsNullOrEmpty(request.DestinationId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationId 不能为空" }, statusCode: 400);
                }

                // 检测目标配置的来源：只允许删除补丁中的目标
                var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{request.DestinationId}";
                var configSource = DetectConfigSource(config, destConfigKey);

                if (configSource != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"目标 {request.DestinationId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, patch =>
                {
                    var cluster = EnsurePatchCluster(patch, request.ClusterId);
                    var dests = EnsurePatchDestinations(cluster);
                    dests.Remove(request.DestinationId);
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 批量删除目标服务器
        app.MapPost("/api/lb/destinations/batch-remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<BatchRemoveDestinationsRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId) || request.DestinationIds == null || request.DestinationIds.Count == 0)
                {
                    return Results.Json(new { success = false, message = "ClusterId 和 DestinationIds 不能为空" }, statusCode: 400);
                }

                // 检测所有目标的配置来源：只允许删除补丁中的目标
                var originalIds = new List<string>();
                foreach (var destId in request.DestinationIds)
                {
                    var destConfigKey = $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{destId}";
                    var configSource = DetectConfigSource(config, destConfigKey);
                    if (configSource != ConfigSource.PatchConfig)
                    {
                        originalIds.Add(destId);
                    }
                }

                if (originalIds.Count > 0)
                {
                    return Results.Json(new { success = false, message = $"以下目标来自默认配置，不能删除: {string.Join(", ", originalIds)}" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, patch =>
                {
                    var cluster = EnsurePatchCluster(patch, request.ClusterId);
                    var dests = EnsurePatchDestinations(cluster);
                    foreach (var destId in request.DestinationIds)
                    {
                        dests.Remove(destId);
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"批量删除目标失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除集群补丁（恢复为原始配置）
        app.MapPost("/api/lb/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveClusterPatchRequest>();
                if (request == null || string.IsNullOrEmpty(request.ClusterId))
                {
                    return Results.Json(new { success = false, message = "ClusterId 不能为空" }, statusCode: 400);
                }

                return await ModifyLbPatch(config, clusters =>
                {
                    if (!clusters.Remove(request.ClusterId))
                    {
                        return $"补丁中不存在集群 {request.ClusterId}";
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 路由管理 API ===============

        // 获取所有路由列表
        app.MapGet("/api/routes", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var rpSection = config.GetSection("ReverseProxy:Routes");
                var routes = new List<object>();
                foreach (var routeSection in rpSection.GetChildren())
                {
                    var routeId = routeSection.Key;
                    var clusterId = routeSection["ClusterId"] ?? "";
                    var order = int.TryParse(routeSection["Order"], out var o) ? o : 0;

                    // Match
                    var matchSection = routeSection.GetSection("Match");
                    var path = matchSection["Path"] ?? "";
                    var hosts = new List<string>();
                    var hostsSection = matchSection.GetSection("Hosts");
                    foreach (var h in hostsSection.GetChildren())
                    {
                        if (h.Value != null) hosts.Add(h.Value);
                    }
                    var methods = new List<string>();
                    var methodsSection = matchSection.GetSection("Methods");
                    foreach (var m in methodsSection.GetChildren())
                    {
                        if (m.Value != null) methods.Add(m.Value);
                    }

                    // 检测配置来源
                    var source = DetectConfigSource(config, $"ReverseProxy:Routes:{routeId}");
                    var sourceText = source == ConfigSource.PatchConfig ? "patch" : "original";

                    routes.Add(new
                    {
                        routeId,
                        clusterId,
                        order,
                        match = new
                        {
                            path,
                            hosts,
                            methods
                        },
                        source = sourceText
                    });
                }

                // 按 Order 排序
                routes.Sort((a, b) =>
                {
                    var orderA = ((dynamic)a).order;
                    var orderB = ((dynamic)b).order;
                    return ((int)orderA).CompareTo((int)orderB);
                });

                return Results.Json(new { success = true, routes, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取路由列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增路由（通过补丁）
        app.MapPost("/api/routes/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddRouteRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检查路由是否已存在（在合并后的配置中）
                var existingRoute = config.GetSection($"ReverseProxy:Routes:{request.RouteId}");
                if (existingRoute.Exists())
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 已存在" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    // 检查补丁中是否也已存在
                    if (routes.ContainsKey(request.RouteId))
                    {
                        return $"路由 {request.RouteId} 已存在于补丁中";
                    }

                    var routePatch = new Dictionary<string, object>();

                    if (request.ClusterId != null)
                        routePatch["ClusterId"] = request.ClusterId;

                    if (request.Order.HasValue)
                    {
                        routePatch["Order"] = request.Order.Value;
                    }
                    else
                    {
                        // 自动分配 Order：从 1000 开始，取所有已有路由 Order 的最大值 +1
                        var usedOrders = new HashSet<int>();
                        foreach (var rs in config.GetSection("ReverseProxy:Routes").GetChildren())
                        {
                            if (int.TryParse(rs["Order"], out var o)) usedOrders.Add(o);
                        }
                        // 也检查补丁中已有路由的 Order
                        foreach (var kv in routes)
                        {
                            if (kv.Value is Dictionary<string, object> rDict && rDict.TryGetValue("Order", out var oVal))
                            {
                                if (oVal is int oi) usedOrders.Add(oi);
                                else if (oVal is string os && int.TryParse(os, out var op)) usedOrders.Add(op);
                            }
                        }
                        var nextOrder = 1000;
                        while (usedOrders.Contains(nextOrder)) nextOrder++;
                        routePatch["Order"] = nextOrder;
                    }

                    var matchPatch = new Dictionary<string, object>();
                    if (request.Match != null)
                    {
                        if (request.Match.Path != null)
                            matchPatch["Path"] = request.Match.Path;
                        if (request.Match.Hosts != null && request.Match.Hosts.Count > 0)
                            matchPatch["Hosts"] = request.Match.Hosts.ToList<object>();
                        if (request.Match.Methods != null && request.Match.Methods.Count > 0)
                            matchPatch["Methods"] = request.Match.Methods.ToList<object>();
                    }

                    // 保证路由必须有 Hosts：若未指定，则尝试从 routeId 中提取端口号生成默认 *:port
                    if (!matchPatch.ContainsKey("Hosts"))
                    {
                        var defaultPort = ExtractPortFromRouteId(request.RouteId);
                        if (defaultPort.HasValue)
                        {
                            matchPatch["Hosts"] = new List<object> { $"*:{defaultPort.Value}" };
                        }
                    }

                    if (matchPatch.Count > 0)
                        routePatch["Match"] = matchPatch;

                    routes[request.RouteId] = routePatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"新增路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新路由配置（通过补丁）
        app.MapPost("/api/routes/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateRouteRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检查路由是否存在
                var routeSection = config.GetSection($"ReverseProxy:Routes:{request.RouteId}");
                if (!routeSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 不存在" }, statusCode: 400);
                }

                // 从当前配置快照需要的字段，然后用补丁覆盖
                return await ModifyRoutePatch(config, routes =>
                {
                    // 获取或创建该路由的补丁节点
                    Dictionary<string, object> routePatch;
                    if (routes.TryGetValue(request.RouteId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            routePatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            routePatch = dict;
                        else
                            routePatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        routePatch = new Dictionary<string, object>();
                    }

                    // 快照当前配置中的基础字段到补丁（如果补丁中还没有）
                    if (!routePatch.ContainsKey("ClusterId"))
                    {
                        var clusterId = routeSection["ClusterId"];
                        if (clusterId != null) routePatch["ClusterId"] = clusterId;
                    }
                    if (!routePatch.ContainsKey("Order"))
                    {
                        var order = routeSection["Order"];
                        if (order != null) routePatch["Order"] = int.TryParse(order, out var o) ? o : (object)order;
                    }
                    if (!routePatch.ContainsKey("Match"))
                    {
                        var matchSection = routeSection.GetSection("Match");
                        var match = new Dictionary<string, object>();
                        var path = matchSection["Path"];
                        if (path != null) match["Path"] = path;

                        var hostsSection = matchSection.GetSection("Hosts");
                        var hostsList = hostsSection.GetChildren().Select(h => h.Value).Where(v => v != null).ToList();
                        if (hostsList.Count > 0) match["Hosts"] = hostsList;

                        var methodsSection = matchSection.GetSection("Methods");
                        var methodsList = methodsSection.GetChildren().Select(m => m.Value).Where(v => v != null).ToList();
                        if (methodsList.Count > 0) match["Methods"] = methodsList;

                        if (match.Count > 0) routePatch["Match"] = match;
                    }

                    // 应用请求中的更新
                    if (request.Order.HasValue)
                    {
                        routePatch["Order"] = request.Order.Value;
                    }
                    if (request.ClusterId != null)
                    {
                        routePatch["ClusterId"] = request.ClusterId;
                    }
                    if (request.Match != null)
                    {
                        Dictionary<string, object> matchPatch;
                        if (routePatch.TryGetValue("Match", out var matchObj) && matchObj is Dictionary<string, object> existMatch)
                        {
                            matchPatch = existMatch;
                        }
                        else
                        {
                            matchPatch = new Dictionary<string, object>();
                        }

                        if (request.Match.Path != null)
                        {
                            matchPatch["Path"] = request.Match.Path;
                        }
                        if (request.Match.Hosts != null)
                        {
                            if (request.Match.Hosts.Count > 0)
                                matchPatch["Hosts"] = request.Match.Hosts.ToList<object>();
                            else
                                matchPatch.Remove("Hosts");
                        }
                        if (request.Match.Methods != null)
                        {
                            if (request.Match.Methods.Count > 0)
                                matchPatch["Methods"] = request.Match.Methods.ToList<object>();
                            else
                                matchPatch.Remove("Methods");
                        }
                        routePatch["Match"] = matchPatch;
                    }

                    // 保证路由有 Hosts：若 Match 中无 Hosts，则从 routeId 提取端口号生成默认 *:port
                    if (routePatch.TryGetValue("Match", out var finalMatchObj) && finalMatchObj is Dictionary<string, object> finalMatch)
                    {
                        if (!finalMatch.ContainsKey("Hosts"))
                        {
                            var defaultPort = ExtractPortFromRouteId(request.RouteId);
                            if (defaultPort.HasValue)
                            {
                                finalMatch["Hosts"] = new List<object> { $"*:{defaultPort.Value}" };
                            }
                        }
                    }

                    routes[request.RouteId] = routePatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除路由（仅支持删除补丁中新增的路由）
        app.MapPost("/api/routes/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRoutePatchRequest>();
                if (request == null || string.IsNullOrEmpty(request.RouteId))
                {
                    return Results.Json(new { success = false, message = "RouteId 不能为空" }, statusCode: 400);
                }

                // 检测来源：如果原始配置中存在，不允许删除（只能删除补丁新增的）
                var source = DetectConfigSource(config, $"ReverseProxy:Routes:{request.RouteId}");
                if (source == ConfigSource.OriginalConfig)
                {
                    return Results.Json(new { success = false, message = $"路由 {request.RouteId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    if (!routes.Remove(request.RouteId))
                    {
                        return $"补丁中不存在路由 {request.RouteId}";
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除路由失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除路由补丁（恢复为原始配置）
        app.MapPost("/api/routes/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveRoutePatchRequest>();
                if (request == null)
                {
                    return Results.Json(new { success = false, message = "请求格式错误" }, statusCode: 400);
                }

                return await ModifyRoutePatch(config, routes =>
                {
                    if (string.IsNullOrEmpty(request.RouteId))
                    {
                        // 清空所有路由补丁
                        routes.Clear();
                    }
                    else
                    {
                        // 删除指定路由补丁
                        if (!routes.Remove(request.RouteId))
                        {
                            return $"补丁中不存在路由 {request.RouteId}";
                        }
                    }
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除路由补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 文件服务配置 API ===============

        // 获取所有文件服务配置
        app.MapGet("/api/fileserver", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var fsSection = config.GetSection("FileServer:Items");
                var items = new List<object>();
                foreach (var itemSection in fsSection.GetChildren())
                {
                    var itemId = itemSection.Key;
                    var prefix = itemSection["Prefix"] ?? "/";
                    var basePath = itemSection["BasePath"] ?? "wwwroot";
                    var browse = bool.TryParse(itemSection["Browse"], out var b) && b;
                    var preCompressed = bool.TryParse(itemSection["PreCompressed"], out var pc) && pc;
                    var maxFileSize = long.TryParse(itemSection["MaxFileSize"], out var mfs) ? mfs : 100L * 1024 * 1024;

                    var tryFiles = new List<string>();
                    var tryFilesSection = itemSection.GetSection("TryFiles");
                    foreach (var tf in tryFilesSection.GetChildren())
                    {
                        if (tf.Value != null) tryFiles.Add(tf.Value);
                    }

                    var defaultFiles = new List<string>();
                    var defaultSection = itemSection.GetSection("Default");
                    foreach (var df in defaultSection.GetChildren())
                    {
                        if (df.Value != null) defaultFiles.Add(df.Value);
                    }

                    var source = DetectConfigSource(config, $"FileServer:Items:{itemId}");
                    items.Add(new
                    {
                        itemId,
                        prefix,
                        basePath,
                        browse,
                        preCompressed,
                        maxFileSize,
                        tryFiles,
                        defaultFiles,
                        source = source == ConfigSource.PatchConfig ? "patch" : "original"
                    });
                }
                return Results.Json(new { success = true, items, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取文件服务配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var existing = config.GetSection($"FileServer:Items:{request.ItemId}");
                if (existing.Exists())
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 已存在" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    if (fileservers.ContainsKey(request.ItemId))
                        return $"文件服务 {request.ItemId} 已存在于补丁中";

                    var item = new Dictionary<string, object>();
                    if (request.Prefix != null) item["Prefix"] = request.Prefix;
                    if (request.BasePath != null) item["BasePath"] = request.BasePath;
                    if (request.Browse.HasValue) item["Browse"] = request.Browse.Value;
                    if (request.PreCompressed.HasValue) item["PreCompressed"] = request.PreCompressed.Value;
                    if (request.MaxFileSize.HasValue) item["MaxFileSize"] = request.MaxFileSize.Value;
                    if (request.TryFiles != null && request.TryFiles.Count > 0)
                        item["TryFiles"] = request.TryFiles.ToList<object>();
                    if (request.DefaultFiles != null && request.DefaultFiles.Count > 0)
                        item["Default"] = request.DefaultFiles.ToList<object>();

                    fileservers[request.ItemId] = item;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"新增文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 更新文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<UpdateFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var itemSection = config.GetSection($"FileServer:Items:{request.ItemId}");
                if (!itemSection.Exists())
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 不存在" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    Dictionary<string, object> itemPatch;
                    if (fileservers.TryGetValue(request.ItemId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            itemPatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            itemPatch = dict;
                        else
                            itemPatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        // 快照当前配置到补丁
                        itemPatch = new Dictionary<string, object>();
                        var section = config.GetSection($"FileServer:Items:{request.ItemId}");
                        var prefix = section["Prefix"]; if (prefix != null) itemPatch["Prefix"] = prefix;
                        var basePath = section["BasePath"]; if (basePath != null) itemPatch["BasePath"] = basePath;
                        var browse = section["Browse"]; if (browse != null) itemPatch["Browse"] = bool.TryParse(browse, out var bv) && bv;
                        var preCompressed = section["PreCompressed"]; if (preCompressed != null) itemPatch["PreCompressed"] = bool.TryParse(preCompressed, out var pcv) && pcv;
                        var maxFileSize = section["MaxFileSize"]; if (maxFileSize != null && long.TryParse(maxFileSize, out var mfv)) itemPatch["MaxFileSize"] = mfv;

                        var tryFilesSection = section.GetSection("TryFiles");
                        var tfList = tryFilesSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                        if (tfList.Count > 0) itemPatch["TryFiles"] = tfList;

                        var defaultSection = section.GetSection("Default");
                        var dfList = defaultSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                        if (dfList.Count > 0) itemPatch["Default"] = dfList;
                    }

                    // 应用更新
                    if (request.Prefix != null) itemPatch["Prefix"] = request.Prefix;
                    if (request.BasePath != null) itemPatch["BasePath"] = request.BasePath;
                    if (request.Browse.HasValue) itemPatch["Browse"] = request.Browse.Value;
                    if (request.PreCompressed.HasValue) itemPatch["PreCompressed"] = request.PreCompressed.Value;
                    if (request.MaxFileSize.HasValue) itemPatch["MaxFileSize"] = request.MaxFileSize.Value;
                    if (request.TryFiles != null)
                    {
                        if (request.TryFiles.Count > 0)
                            itemPatch["TryFiles"] = request.TryFiles.ToList<object>();
                        else
                            itemPatch.Remove("TryFiles");
                    }
                    if (request.DefaultFiles != null)
                    {
                        if (request.DefaultFiles.Count > 0)
                            itemPatch["Default"] = request.DefaultFiles.ToList<object>();
                        else
                            itemPatch.Remove("Default");
                    }

                    fileservers[request.ItemId] = itemPatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除文件服务（仅补丁中新增的）
        app.MapPost("/api/fileserver/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveFileServerRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var source = DetectConfigSource(config, $"FileServer:Items:{request.ItemId}");
                if (source != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifyFileServerPatch(config, fileservers =>
                {
                    if (!fileservers.Remove(request.ItemId))
                        return $"补丁中不存在文件服务 {request.ItemId}";
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除文件服务失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除所有文件服务补丁
        app.MapPost("/api/fileserver/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                return await ModifyFileServerPatch(config, fileservers =>
                {
                    fileservers.Clear();
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除文件服务补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 简单响应配置 API ===============

        // 获取所有简单响应配置
        app.MapGet("/api/simpleres", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var srSection = config.GetSection("SimpleRes:Items");
                var items = new List<object>();
                foreach (var itemSection in srSection.GetChildren())
                {
                    var itemId = itemSection.Key;
                    var body = itemSection["Body"] ?? "";
                    var contentType = itemSection["ContentType"] ?? "text/plain";
                    var statusCode = int.TryParse(itemSection["StatusCode"], out var sc) ? sc : 200;
                    var charset = itemSection["Charset"] ?? "utf-8";
                    var showReq = bool.TryParse(itemSection["ShowReq"], out var sr) && sr;

                    var headers = new Dictionary<string, string>();
                    var headersSection = itemSection.GetSection("Headers");
                    foreach (var h in headersSection.GetChildren())
                    {
                        if (h.Value != null) headers[h.Key] = h.Value;
                    }

                    var source = DetectConfigSource(config, $"SimpleRes:Items:{itemId}");
                    items.Add(new
                    {
                        itemId,
                        body,
                        contentType,
                        statusCode,
                        charset,
                        showReq,
                        headers,
                        source = source == ConfigSource.PatchConfig ? "patch" : "original"
                    });
                }
                return Results.Json(new { success = true, items, timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取简单响应配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 新增或更新简单响应配置（通过补丁），存在则自动更新
        app.MapPost("/api/simpleres/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    Dictionary<string, object> itemPatch;
                    // 已存在于补丁中 → 在原有基础上更新
                    if (simpleres.TryGetValue(request.ItemId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            itemPatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            itemPatch = dict;
                        else
                            itemPatch = new Dictionary<string, object>();
                    }
                    // 已存在于原始配置 → 快照到补丁再更新
                    else if (config.GetSection($"SimpleRes:Items:{request.ItemId}").Exists())
                    {
                        itemPatch = new Dictionary<string, object>();
                        var section = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                        var body = section["Body"]; if (body != null) itemPatch["Body"] = body;
                        var ct = section["ContentType"]; if (ct != null) itemPatch["ContentType"] = ct;
                        var sc = section["StatusCode"]; if (sc != null && int.TryParse(sc, out var scv)) itemPatch["StatusCode"] = scv;
                        var cs = section["Charset"]; if (cs != null) itemPatch["Charset"] = cs;
                        var srq = section["ShowReq"]; if (srq != null) itemPatch["ShowReq"] = bool.TryParse(srq, out var srv) && srv;

                        var headersSection = section.GetSection("Headers");
                        var hDict = new Dictionary<string, object>();
                        foreach (var h in headersSection.GetChildren())
                        {
                            if (h.Value != null) hDict[h.Key] = h.Value;
                        }
                        if (hDict.Count > 0) itemPatch["Headers"] = hDict;
                    }
                    // 全新项
                    else
                    {
                        itemPatch = new Dictionary<string, object>();
                    }

                    // 应用字段
                    if (request.Body != null) itemPatch["Body"] = request.Body;
                    if (request.ContentType != null) itemPatch["ContentType"] = request.ContentType;
                    if (request.StatusCode.HasValue) itemPatch["StatusCode"] = request.StatusCode.Value;
                    if (request.Charset != null) itemPatch["Charset"] = request.Charset;
                    if (request.ShowReq.HasValue) itemPatch["ShowReq"] = request.ShowReq.Value;
                    if (request.Headers != null)
                    {
                        if (request.Headers.Count > 0)
                            itemPatch["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        else
                            itemPatch.Remove("Headers");
                    }

                    simpleres[request.ItemId] = itemPatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 兼容旧接口：/api/simpleres/update 转发到 /api/simpleres/add 同一逻辑
        app.MapPost("/api/simpleres/update", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                // 更新接口要求项必须已存在
                var existing = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                if (!existing.Exists())
                {
                    return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 不存在" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    Dictionary<string, object> itemPatch;
                    if (simpleres.TryGetValue(request.ItemId, out var existObj))
                    {
                        if (existObj is System.Text.Json.JsonElement je)
                            itemPatch = JsonElementToDict(je);
                        else if (existObj is Dictionary<string, object> dict)
                            itemPatch = dict;
                        else
                            itemPatch = new Dictionary<string, object>();
                    }
                    else
                    {
                        itemPatch = new Dictionary<string, object>();
                        var section = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                        var body = section["Body"]; if (body != null) itemPatch["Body"] = body;
                        var ct = section["ContentType"]; if (ct != null) itemPatch["ContentType"] = ct;
                        var sc = section["StatusCode"]; if (sc != null && int.TryParse(sc, out var scv)) itemPatch["StatusCode"] = scv;
                        var cs = section["Charset"]; if (cs != null) itemPatch["Charset"] = cs;
                        var srq = section["ShowReq"]; if (srq != null) itemPatch["ShowReq"] = bool.TryParse(srq, out var srv) && srv;

                        var headersSection = section.GetSection("Headers");
                        var hDict = new Dictionary<string, object>();
                        foreach (var h in headersSection.GetChildren())
                        {
                            if (h.Value != null) hDict[h.Key] = h.Value;
                        }
                        if (hDict.Count > 0) itemPatch["Headers"] = hDict;
                    }

                    if (request.Body != null) itemPatch["Body"] = request.Body;
                    if (request.ContentType != null) itemPatch["ContentType"] = request.ContentType;
                    if (request.StatusCode.HasValue) itemPatch["StatusCode"] = request.StatusCode.Value;
                    if (request.Charset != null) itemPatch["Charset"] = request.Charset;
                    if (request.ShowReq.HasValue) itemPatch["ShowReq"] = request.ShowReq.Value;
                    if (request.Headers != null)
                    {
                        if (request.Headers.Count > 0)
                            itemPatch["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                        else
                            itemPatch.Remove("Headers");
                    }

                    simpleres[request.ItemId] = itemPatch;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除简单响应（仅补丁中新增的）
        app.MapPost("/api/simpleres/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveSimpleResRequest>();
                if (request == null || string.IsNullOrEmpty(request.ItemId))
                {
                    return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
                }

                var source = DetectConfigSource(config, $"SimpleRes:Items:{request.ItemId}");
                if (source != ConfigSource.PatchConfig)
                {
                    return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
                }

                return await ModifySimpleResPatch(config, simpleres =>
                {
                    if (!simpleres.Remove(request.ItemId))
                        return $"补丁中不存在简单响应 {request.ItemId}";
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除简单响应失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除所有简单响应补丁
        app.MapPost("/api/simpleres/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                return await ModifySimpleResPatch(config, simpleres =>
                {
                    simpleres.Clear();
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除简单响应补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 清空所有补丁 ===============

        app.MapPost("/api/patch/clear", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                var patchPath = LbPatchConfig.GetPatchFilePath();
                await File.WriteAllTextAsync(patchPath, "{}", Encoding.UTF8);

                if (config is IConfigurationRoot configRoot)
                {
                    configRoot.Reload();
                }

                return Results.Json(new { success = true, message = "所有补丁已清除" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"清除补丁失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 监听端口管理 ===============

        // 添加监听端口
        app.MapPost("/api/listen/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);

                var host = request.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "0.0.0.0" : "0.0.0.0";
                var port = request.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                var isHttps = request.TryGetProperty("isHttps", out var httpsEl) && httpsEl.GetBoolean();
                int? autoHttpsPort = request.TryGetProperty("autoHttpsPort", out var ahpEl) && ahpEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? ahpEl.GetInt32() : null;

                if (port <= 0 || port > 65535)
                {
                    return Results.Json(new { success = false, message = "端口号无效（1-65535）" }, statusCode: 400);
                }

                // 检查端口是否已存在
                if (wafInfos.Listens.Any(l => l.Port == port && l.Host == host))
                {
                    return Results.Json(new { success = false, message = $"监听 {host}:{port} 已存在" }, statusCode: 400);
                }

                // 计算下一个索引
                var nextIndex = wafInfos.Listens.Count;

                return await ModifyListenPatch(config, listens =>
                {
                    // 检查补丁中是否已有更大的索引
                    var maxIdx = nextIndex - 1;
                    foreach (var key in listens.Keys)
                    {
                        if (int.TryParse(key, out var idx) && idx > maxIdx) maxIdx = idx;
                    }
                    var newIdx = (maxIdx + 1).ToString();

                    var listenData = new Dictionary<string, object>
                    {
                        ["Host"] = host,
                        ["Port"] = port,
                        ["IsHttps"] = isHttps
                    };
                    if (autoHttpsPort.HasValue)
                    {
                        listenData["AutoHttpsPort"] = autoHttpsPort.Value;
                    }
                    listens[newIdx] = listenData;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加监听端口失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除监听端口（仅限补丁添加的）
        app.MapPost("/api/listen/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);

                var port = request.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                var host = request.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "" : "";

                if (port <= 0)
                {
                    return Results.Json(new { success = false, message = "端口号无效" }, statusCode: 400);
                }

                return await ModifyListenPatch(config, listens =>
                {
                    string? removeKey = null;
                    foreach (var (key, val) in listens)
                    {
                        Dictionary<string, object>? listenDict = null;
                        if (val is System.Text.Json.JsonElement je)
                            listenDict = JsonElementToDict(je);
                        else if (val is Dictionary<string, object> d)
                            listenDict = d;

                        if (listenDict == null) continue;

                        var lPort = listenDict.TryGetValue("Port", out var pObj) ? Convert.ToInt32(pObj) : 0;
                        var lHost = listenDict.TryGetValue("Host", out var hObj) ? hObj?.ToString() ?? "" : "";

                        if (lPort == port && (string.IsNullOrEmpty(host) || lHost == host))
                        {
                            removeKey = key;
                            break;
                        }
                    }

                    if (removeKey == null)
                    {
                        return $"补丁中不存在监听 {host}:{port}，原始配置中的监听不能在此删除";
                    }

                    listens.Remove(removeKey);
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除监听端口失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 插件管理 API ===============

        app.MapGet("/api/plugins", (IPluginManager pluginManager) =>
        {
            try
            {
                var plugins = pluginManager.GetAllPlugins().Select(p =>
                {
                    var meta = p.Metadata;
                    var pluginId = meta.Id;
                    var actions = p.GetActions().Select(a => new
                    {
                        id = a.Id,
                        name = a.Name,
                        type = a.Type,
                        description = a.Description,
                    }).ToList();

                    return new
                    {
                        id = pluginId,
                        name = meta.Name,
                        version = meta.Version,
                        description = meta.Description,
                        author = meta.Author,
                        priority = meta.Priority.ToString(),
                        state = pluginManager.GetPluginState(pluginId).ToString(),
                        isEnabled = pluginManager.GetPluginState(pluginId) == PluginState.Running
                                 || pluginManager.GetPluginState(pluginId) == PluginState.Initialized,
                        isSystem = pluginManager.IsSystemPlugin(pluginId),
                        enabledByDefault = meta.EnabledByDefault,
                        hasOptions = meta.DefaultOptions != null,
                        actions,
                    };
                }).OrderBy(p => p.isSystem ? 0 : 1).ThenBy(p => p.name).ToList();

                return Results.Json(new { success = true, plugins });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取插件列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/plugins/toggle", async (HttpContext ctx, IPluginManager pluginManager) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<PluginToggleRequest>();
                if (request == null || string.IsNullOrEmpty(request.PluginId))
                {
                    return Results.Json(new { success = false, message = "PluginId 不能为空" }, statusCode: 400);
                }

                var plugin = pluginManager.GetPlugin(request.PluginId);
                if (plugin == null)
                {
                    return Results.Json(new { success = false, message = $"插件 {request.PluginId} 不存在" }, statusCode: 404);
                }

                var currentState = pluginManager.GetPluginState(request.PluginId);
                var isRunning = currentState == PluginState.Running || currentState == PluginState.Initialized;

                if (isRunning)
                {
                    await pluginManager.DisablePluginAsync(request.PluginId);
                }
                else
                {
                    await pluginManager.EnablePluginAsync(request.PluginId);
                }

                var newState = pluginManager.GetPluginState(request.PluginId);
                return Results.Json(new
                {
                    success = true,
                    message = isRunning ? "插件已禁用" : "插件已启用",
                    state = newState.ToString(),
                    isEnabled = newState == PluginState.Running || newState == PluginState.Initialized,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"切换插件状态失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 插件配置读取 ===============

        app.MapGet("/api/plugins/{pluginId}/config", (string pluginId, IConfiguration config, IPluginManager pluginManager) =>
        {
            try
            {
                var plugin = pluginManager.GetPlugin(pluginId);
                if (plugin == null)
                {
                    return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);
                }

                // 1. 创建一个新的 Options 实例来获取原始默认值
                //    （因为 Metadata.DefaultOptions 是运行时实例，已被 BindOptionsFromConfig 覆盖）
                var defaults = new Dictionary<string, string>();
                var defaultOptions = plugin.Metadata.DefaultOptions;
                if (defaultOptions != null)
                {
                    var freshDefaults = Activator.CreateInstance(defaultOptions.GetType());
                    if (freshDefaults != null)
                    {
                        SerializeOptionsToFlat(freshDefaults, "", defaults);
                    }
                }

                // 2. 从运行时 DefaultOptions 实例序列化当前配置
                //    （它就是运行时 Options，已被 BindOptionsFromConfig 绑定过）
                var current = new Dictionary<string, string>();
                if (defaultOptions != null)
                {
                    SerializeOptionsToFlat(defaultOptions, "", current);
                }

                // 3. 提取属性的中文标签
                var labels = defaultOptions != null
                    ? ExtractPropertyLabels(defaultOptions.GetType())
                    : new Dictionary<string, string>();

                // 4. 提取 List<T> 元素类型的字段默认值（用于前端推断字段类型和初始值）
                var fieldDefaults = defaultOptions != null
                    ? ExtractListElementDefaults(defaultOptions.GetType())
                    : new Dictionary<string, string>();

                return Results.Json(new { success = true, pluginId, config = current, defaults, labels, fieldDefaults });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取插件配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 插件配置更新 ===============

        app.MapPost("/api/plugins/{pluginId}/config", async (string pluginId, HttpContext ctx, IConfiguration config, IPluginManager pluginManager) =>
        {
            try
            {
                var plugin = pluginManager.GetPlugin(pluginId);
                if (plugin == null)
                {
                    return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);
                }

                var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                if (body == null)
                {
                    return Results.Json(new { success = false, message = "请求体不能为空" }, statusCode: 400);
                }

                var result = await ModifyPluginConfigPatch(config, pluginId, pluginConfigs =>
                {
                    // 将 flat key-value 转为嵌套结构后写入
                    var nested = FlatKeysToNestedDict(body);
                    pluginConfigs[pluginId] = nested;
                    return null;
                });

                // 配置已重载，同步绑定到 Metadata.DefaultOptions 实例上
                // 注意：Bind() 对 List/Dictionary 是追加而非替换，必须先清空
                if (plugin.Metadata.DefaultOptions != null)
                {
                    ClearCollectionProperties(plugin.Metadata.DefaultOptions);
                    config.GetSection($"Plugins:{pluginId}").Bind(plugin.Metadata.DefaultOptions);
                }

                return result;
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存插件配置失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 插件操作（统一框架） ===============

        // 获取插件支持的操作列表
        app.MapGet("/api/plugins/{pluginId}/actions", (string pluginId, IPluginManager pluginManager) =>
        {
            try
            {
                var plugin = pluginManager.GetPlugin(pluginId);
                if (plugin == null)
                    return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);

                var actions = plugin.GetActions().Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    type = a.Type,
                    description = a.Description,
                }).ToList();

                return Results.Json(new { success = true, pluginId, actions });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取插件操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 执行插件操作
        app.MapPost("/api/plugins/{pluginId}/actions/{actionId}", async (string pluginId, string actionId, HttpContext ctx, IPluginManager pluginManager) =>
        {
            try
            {
                var plugin = pluginManager.GetPlugin(pluginId);
                if (plugin == null)
                    return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);

                var actions = plugin.GetActions();
                if (!actions.Any(a => a.Id == actionId))
                    return Results.Json(new { success = false, message = $"插件 {pluginId} 不支持操作: {actionId}" }, statusCode: 404);

                // 读取参数
                Dictionary<string, string>? parameters = null;
                if (ctx.Request.ContentLength > 0)
                {
                    parameters = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                }

                var result = await plugin.ExecuteActionAsync(actionId, parameters);
                return Results.Json(new { success = result.Success, message = result.Message, data = result.Data });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"执行插件操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 证书管理 ===============

        app.MapGet("/api/settings/certs", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                // ACME 证书目录
                var acmeOpts = new AcmeOptions();
                config.GetSection("Acme").Bind(acmeOpts);
                var acmeCertDir = Path.GetFullPath(acmeOpts.CertificatePath);

                var certList = new List<object>();
                foreach (var c in wafInfos.Certs)
                {
                    var domain = c.Host;
                    var pemFile = c.PemFile;
                    var certType = "uploaded";
                    var issuer = "";
                    var notAfter = "";

                    // 判断类型：PEM 路径在 ACME 目录下 → acme
                    try
                    {
                        var fullPemPath = Path.GetFullPath(pemFile);
                        if (fullPemPath.StartsWith(acmeCertDir, StringComparison.OrdinalIgnoreCase))
                        {
                            certType = "acme";
                        }
                    }
                    catch { }

                    // 解析证书详细信息
                    try
                    {
                        if (File.Exists(pemFile))
                        {
                            var pemText = File.ReadAllText(pemFile);
                            using var cert = X509Certificate2.CreateFromPem(pemText);
                            issuer = cert.Issuer;
                            notAfter = cert.NotAfter.ToString("yyyy-MM-dd");
                        }
                    }
                    catch { }

                    certList.Add(new
                    {
                        domain,
                        type = certType,
                        issuer,
                        notAfter,
                        pemFile = Path.GetFileName(pemFile),
                    });
                }

                // 额外扫描 certs 目录中 ACME 签发但不在 wafInfos.Certs 中的证书
                var certsDir = Path.GetFullPath("certs");
                if (Directory.Exists(certsDir))
                {
                    var existingPemFiles = new HashSet<string>(
                        certList.Select(c => ((dynamic)c).pemFile as string ?? ""),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var pemFile in Directory.GetFiles(certsDir, "*.pem"))
                    {
                        var fileName = Path.GetFileName(pemFile);
                        if (existingPemFiles.Contains(fileName)) continue;
                        if (fileName == "account.pem") continue;

                        var keyFile = Path.ChangeExtension(pemFile, ".key");
                        if (!File.Exists(keyFile)) continue;

                        try
                        {
                            var pemText = File.ReadAllText(pemFile);
                            using var cert = X509Certificate2.CreateFromPem(pemText);

                            // 从文件名反推域名
                            var domainName = Path.GetFileNameWithoutExtension(fileName)
                                .Replace("_wildcard_", "*");

                            certList.Add(new
                            {
                                domain = domainName,
                                type = "acme",
                                issuer = cert.Issuer,
                                notAfter = cert.NotAfter.ToString("yyyy-MM-dd"),
                                pemFile = fileName,
                            });
                        }
                        catch { }
                    }
                }

                return Results.Json(new { success = true, certs = certList });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取证书列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/upload", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CertUploadRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Domain)
                    || string.IsNullOrWhiteSpace(request.PemContent) || string.IsNullOrWhiteSpace(request.KeyContent))
                {
                    return Results.Json(new { success = false, message = "域名、证书内容和密钥内容不能为空" }, statusCode: 400);
                }

                // 验证 PEM 格式
                try
                {
                    using var _ = X509Certificate2.CreateFromPem(request.PemContent, request.KeyContent);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { success = false, message = $"证书格式无效: {ex.Message}" }, statusCode: 400);
                }

                // 保存文件
                var certsDir = Path.Combine(Directory.GetCurrentDirectory(), "certs");
                if (!Directory.Exists(certsDir)) Directory.CreateDirectory(certsDir);

                var safeDomain = request.Domain.Replace("*", "_wildcard_").Replace(":", "_");
                var pemPath = Path.Combine(certsDir, $"{safeDomain}.pem");
                var keyPath = Path.Combine(certsDir, $"{safeDomain}.key");

                await File.WriteAllTextAsync(pemPath, request.PemContent);
                await File.WriteAllTextAsync(keyPath, request.KeyContent);

                // 记审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"上传证书: {request.Domain}", clientIp);

                return Results.Json(new { success = true, message = $"证书已保存，需要重载配置后生效", pemFile = $"{safeDomain}.pem", keyFile = $"{safeDomain}.key" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"上传证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/delete", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CertDeleteRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.PemFile))
                {
                    return Results.Json(new { success = false, message = "证书文件名不能为空" }, statusCode: 400);
                }

                // 查找对应证书（先查 wafInfos，再查 certs 目录）
                var cert = wafInfos.Certs.FirstOrDefault(c => Path.GetFileName(c.PemFile) == request.PemFile);
                var domainForLog = "";
                if (cert != null)
                {
                    domainForLog = cert.Host;
                    if (File.Exists(cert.PemFile)) File.Delete(cert.PemFile);
                    if (!string.IsNullOrEmpty(cert.KeyFile) && File.Exists(cert.KeyFile)) File.Delete(cert.KeyFile);
                }
                else
                {
                    // 尝试从 certs 目录直接删除（ACME 签发的独立证书）
                    var pemPath = Path.Combine("certs", request.PemFile);
                    var keyPath = Path.ChangeExtension(pemPath, ".key");
                    if (!File.Exists(pemPath))
                        return Results.Json(new { success = false, message = "未找到该证书" }, statusCode: 404);

                    domainForLog = Path.GetFileNameWithoutExtension(request.PemFile).Replace("_wildcard_", "*");
                    File.Delete(pemPath);
                    if (File.Exists(keyPath)) File.Delete(keyPath);
                }

                // 记审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"删除证书: {domainForLog} ({request.PemFile})", clientIp);

                return Results.Json(new { success = true, message = "证书已删除，需要重载配置后生效" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // ---- ACME 手动申请/续期 ----

        app.MapGet("/api/settings/acme-email", (IAcmeService acmeService) =>
        {
            return Results.Json(new { success = true, email = acmeService.GetSavedEmail() ?? "" });
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/acme-apply", async (HttpContext ctx, IAcmeService acmeService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AcmeApplyRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Domain) || string.IsNullOrWhiteSpace(request.Email))
                {
                    return Results.Json(new { success = false, errorMessage = "域名和邮箱不能为空" }, statusCode: 400);
                }

                var result = await acmeService.ManualRequestCertificateAsync(request.Domain, request.Email, ctx.RequestAborted);

                // 审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"申请免费证书: {request.Domain} ({(result.Success ? "成功" : "失败")})", clientIp);

                return Results.Json(result);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { success = false, errorMessage = "操作已取消" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, errorMessage = $"申请证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/acme-renew", async (HttpContext ctx, IAcmeService acmeService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AcmeRenewRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Domain))
                {
                    return Results.Json(new { success = false, errorMessage = "域名不能为空" }, statusCode: 400);
                }

                // 优先用请求中的邮箱，否则用已保存的
                var email = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : acmeService.GetSavedEmail();
                if (string.IsNullOrWhiteSpace(email))
                {
                    return Results.Json(new { success = false, errorMessage = "请先配置联系邮箱" }, statusCode: 400);
                }

                var result = await acmeService.ManualRequestCertificateAsync(request.Domain, email, ctx.RequestAborted);

                // 审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"续期证书: {request.Domain} ({(result.Success ? "成功" : "失败")})", clientIp);

                return Results.Json(result);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { success = false, errorMessage = "操作已取消" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, errorMessage = $"续期证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 控制台信息 ===============

        app.MapGet("/api/settings/console", (HttpContext ctx) =>
        {
            try
            {
                var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
                var cl = wafInfos.GetControlListen();

                return Results.Json(new
                {
                    success = true,
                    username = authService.GetCurrentUsername(),
                    lastLoginAt = authService.GetLastLoginAt(),
                    controlListen = new { host = cl.Host, port = cl.Port }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取控制台信息失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 审计日志 ===============

        app.MapGet("/api/settings/audit-logs", (HttpContext ctx, int offset = 0, int limit = 50) =>
        {
            try
            {
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var (items, total) = auditLog.GetLogs(offset, limit);

                return Results.Json(new
                {
                    success = true,
                    items = items.Select(e => new
                    {
                        username = e.Username,
                        action = e.Action,
                        ip = e.Ip,
                        timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                    }),
                    total
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取审计日志失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 运行日志 ===============

        app.MapGet("/api/settings/runtime-logs", (HttpContext ctx, int offset = 0, int limit = 20) =>
        {
            try
            {
                var runtimeLog = ctx.RequestServices.GetRequiredService<LyWaf.Services.DuckDb.RuntimeLogService>();
                var (items, total) = runtimeLog.GetLogs(offset, limit);

                return Results.Json(new
                {
                    success = true,
                    items = items.Select(e => new
                    {
                        id = e.Id,
                        startTime = e.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        stopTime = e.StopTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        exitReason = e.ExitReason,
                        durationSeconds = e.DurationSeconds,
                        peakMemoryMb = Math.Round(e.PeakMemoryMb, 1),
                        finalMemoryMb = Math.Round(e.FinalMemoryMb, 1),
                        gcGen0 = e.GcGen0,
                        gcGen1 = e.GcGen1,
                        gcGen2 = e.GcGen2,
                        totalRequests = e.TotalRequests,
                        totalIntercepts = e.TotalIntercepts,
                        dotnetVersion = e.DotnetVersion,
                        osDescription = e.OsDescription,
                        processId = e.ProcessId
                    }),
                    total
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取运行日志失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== DuckDB 历史查询 API ===============

        app.MapGet("/api/traffic/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var granularity = ctx.Request.Query["granularity"].FirstOrDefault() ?? "hour";

                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetTrafficHistory(from, to, granularity);
                return Results.Json(new { success = true, data, from, to, granularity });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/security/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-7);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetSecurityTimeSlots(from, to);
                return Results.Json(new
                {
                    success = true,
                    data = data.Select(s => new
                    {
                        time = s.Time,
                        wafIntercept = s.WafIntercept,
                        blacklistBlock = s.BlacklistBlock,
                        ccAttack = s.CcAttack,
                        crawlerDetect = s.CrawlerDetect,
                        geoBlock = s.GeoBlock,
                        rateLimit = s.RateLimit,
                        total = s.Total,
                    }),
                    from, to,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/timing/history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var pathFilter = ctx.Request.Query["path"].FirstOrDefault();
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetApiTimingHistory(from, to, pathFilter);
                return Results.Json(new
                {
                    success = true,
                    data = data.Select(item => new
                    {
                        snapshotTime = item.SnapshotTime,
                        path = item.Path,
                        method = item.Method,
                        backend = item.Backend,
                        originalHost = item.OriginalHost,
                        requestCount = item.RequestCount,
                        avgTotalTime = item.RequestCount > 0 ? Math.Round((double)item.TotalTime / item.RequestCount, 2) : 0,
                        avgBackendTime = item.RequestCount > 0 ? Math.Round((double)item.BackendTime / item.RequestCount, 2) : 0,
                        minTotalTime = item.MinTotalTime,
                        maxTotalTime = item.MaxTotalTime,
                        statusCodeCounts = item.StatusCodeCounts,
                    }),
                    from, to,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/stats/endpoint-history", (HttpContext ctx, LyWaf.Services.DuckDb.IDuckDbQueryService queryService) =>
        {
            try
            {
                if (!queryService.IsEnabled)
                    return Results.Json(new { success = false, message = "DuckDB 未启用" });

                var statType = ctx.Request.Query["type"].FirstOrDefault() ?? "dest";
                var fromStr = ctx.Request.Query["from"].FirstOrDefault();
                var toStr = ctx.Request.Query["to"].FirstOrDefault();
                var topN = int.TryParse(ctx.Request.Query["topN"].FirstOrDefault(), out var n) ? n : 50;
                var from = DateTime.TryParse(fromStr, out var f) ? f.ToUniversalTime() : DateTime.UtcNow.AddDays(-1);
                var to = DateTime.TryParse(toStr, out var t) ? t.ToUniversalTime() : DateTime.UtcNow;

                var data = queryService.GetEndpointStatsHistory(statType, from, to, topN);
                return Results.Json(new { success = true, data, from, to, statType });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 参数配置 API ===============

        app.MapGet("/api/param", (HttpContext ctx) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;
            return Results.Json(new
            {
                success = true,
                isFirstServer = SharedData.IsFirstServer,
                localAddress = opts.LocalAddress ?? "",
            });
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/param", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<ParamUpdateRequest>();
                if (body == null)
                    return Results.Json(new { success = false, message = "无效的请求体" }, statusCode: 400);

                var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;

                if (body.IsFirstServer.HasValue)
                {
                    SharedData.IsFirstServer = body.IsFirstServer.Value;
                }

                if (body.LocalAddress != null)
                {
                    opts.LocalAddress = string.IsNullOrWhiteSpace(body.LocalAddress) ? null : body.LocalAddress.Trim();
                }

                return Results.Json(new
                {
                    success = true,
                    isFirstServer = SharedData.IsFirstServer,
                    localAddress = opts.LocalAddress ?? "",
                    message = "参数配置已更新"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/param/env", (HttpContext ctx) =>
        {
            var envVars = Environment.GetEnvironmentVariables();
            var list = new List<object>();
            foreach (System.Collections.DictionaryEntry entry in envVars)
            {
                list.Add(new { key = entry.Key?.ToString(), value = entry.Value?.ToString() });
            }
            list.Sort((a, b) => string.Compare(
                ((dynamic)a).key, ((dynamic)b).key, StringComparison.OrdinalIgnoreCase));
            return Results.Json(new { success = true, items = list, total = list.Count });
        }).RequireHost($"*:{controlPort}");

        // =============== 抓包管理 API ===============

        // 设置 CRL 分发点 URL（嵌入到 Pcap 签发的证书中，供客户端验证吊销状态）
        // CRL 由代理端口直接提供（ProxyHandler.ServeCrlAsync），使用本机网络 IP
        {
            var pcapProvider = app.Services.GetRequiredService<PcapCertProvider>();
            var proxyOptions = app.Services.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>().CurrentValue;

            // 找到第一个启用了 Pcap 的代理端口
            var pcapPort = proxyOptions.Ports
                .Where(p => p.Value.EnablePcap && (p.Value.EnableHttp || p.Value.EnableHttps))
                .Select(p => {
                    var lastColon = p.Key.LastIndexOf(':');
                    return lastColon > 0 && int.TryParse(p.Key[(lastColon + 1)..], out var port) ? port
                         : int.TryParse(p.Key, out var port2) ? port2 : 0;
                })
                .FirstOrDefault(p => p > 0);

            // 没有 Pcap 端口就用 control 端口作为回退
            if (pcapPort == 0) pcapPort = controlPort;

            var globalOpts = app.Services.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;
            var localIp = GetLocalNetworkIp(globalOpts);
            pcapProvider.CrlUrl = $"http://{localIp}:{pcapPort}{ProxyHandler.CrlPath}";
        }

        // CRL 下载（控制端口回退，不需要认证）
        app.MapGet("/api/pcap/ca.crl", (HttpContext ctx) =>
        {
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
            var crlPath = pcapCertProvider.CaCrlPath;
            if (!File.Exists(crlPath))
                return Results.NotFound();

            return Results.File(crlPath, "application/pkix-crl", "proxy-ca.crl");
        }).RequireHost($"*:{controlPort}");

        // 获取抓包状态
        app.MapGet("/api/pcap/status", (HttpContext ctx) =>
        {
            var proxyOptions = ctx.RequestServices.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>().CurrentValue;
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();

            var ports = proxyOptions.Ports.Select(p => new
            {
                port = p.Key,
                enablePcap = p.Value.EnablePcap,
                enableHttp = p.Value.EnableHttp,
                enableHttps = p.Value.EnableHttps,
                enableSocks5 = p.Value.EnableSocks5,
            }).ToList();

            return Results.Json(new
            {
                success = true,
                proxyEnabled = proxyOptions.Enabled,
                caCertExists = File.Exists(pcapCertProvider.CaCertPath),
                ports,
            });
        }).RequireHost($"*:{controlPort}");

        // 切换抓包开关
        app.MapPost("/api/pcap/toggle", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<PcapToggleRequest>();
                if (body == null || string.IsNullOrEmpty(body.PortKey))
                    return Results.Json(new { success = false, message = "无效的请求" }, statusCode: 400);

                var proxyOptionsMonitor = ctx.RequestServices.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>();
                var options = proxyOptionsMonitor.CurrentValue;
                if (!options.Ports.TryGetValue(body.PortKey, out var portConfig))
                    return Results.Json(new { success = false, message = $"端口 {body.PortKey} 不存在" }, statusCode: 404);

                portConfig.EnablePcap = body.Enabled;

                // 如果启用 Pcap 且 CA 证书未初始化，初始化证书
                if (body.Enabled)
                {
                    var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
                    pcapCertProvider.Initialize();
                }

                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown",
                    $"切换抓包: 端口 {body.PortKey} → {(body.Enabled ? "启用" : "禁用")}",
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "");

                return Results.Json(new { success = true, enabled = body.Enabled, message = body.Enabled ? "抓包已启用" : "抓包已禁用" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 下载 CA 证书
        app.MapGet("/api/pcap/ca-cert", (HttpContext ctx) =>
        {
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
            var certPath = pcapCertProvider.CaCertPath;
            if (!File.Exists(certPath))
                return Results.Json(new { success = false, message = "CA 证书不存在，请先启用抓包功能" }, statusCode: 404);

            return Results.File(certPath, "application/x-x509-ca-cert", "proxy-ca.crt");
        }).RequireHost($"*:{controlPort}");

        // 列出抓包日志文件
        app.MapGet("/api/pcap/logs/files", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var files = LogUtil.ListLogFiles(dir, "pcap_*.log");
            return Results.Json(new { success = true, files });
        }).RequireHost($"*:{controlPort}");

        // 获取日志文件中所有不重复的 Host
        app.MapGet("/api/pcap/logs/hosts", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, hosts = Array.Empty<string>() });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, hosts = Array.Empty<string>() });

            var hosts = await LogUtil.GetUniqueHostsAsync(filePath);
            return Results.Json(new { success = true, hosts });
        }).RequireHost($"*:{controlPort}");

        // 读取抓包日志条目
        app.MapGet("/api/pcap/logs/entries", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, message = "无效的文件名" });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, message = $"文件不存在: {fileName}" });

            _ = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault() ?? "0", out var offset);
            _ = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault() ?? "20", out var limit);
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var host = ctx.Request.Query["host"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st3) ? st3 : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et3) ? et3 : null;

            var (entries, total) = await LogUtil.ParseLogFileAsync(filePath, offset, Math.Clamp(limit, 1, 100), search, startTime, endTime, host);
            return Results.Json(new
            {
                success = true,
                entries,
                total,
                offset,
                limit,
                fileName,
            });
        }).RequireHost($"*:{controlPort}");

        return app;
    }

    /// <summary>
    /// 递归展平 IConfigurationSection 为 flat key-value
    /// </summary>
    private static void FlattenConfigSection(IConfigurationSection section, string prefix, Dictionary<string, string> result)
    {
        if (section.Value != null)
        {
            result[prefix] = section.Value;
        }

        foreach (var child in section.GetChildren())
        {
            FlattenConfigSection(child, $"{prefix}:{child.Key}", result);
        }
    }

    /// <summary>
    /// 清空对象上所有 List/Dictionary 属性，防止 Bind() 追加导致重复
    /// </summary>
    private static void ClearCollectionProperties(object obj)
    {
        if (obj == null) return;
        foreach (var prop in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            object? value;
            try { value = prop.GetValue(obj); } catch { continue; }
            if (value is System.Collections.IList list)
                list.Clear();
            else if (value is System.Collections.IDictionary dict)
                dict.Clear();
        }
    }

    /// <summary>
    /// 将 Options 对象实例的公共属性序列化为 flat key-value（支持嵌套对象、列表、字典）
    /// 跳过仅有 setter 的别名属性（如 ConfigurationKeyName 别名）
    /// </summary>
    private static void SerializeOptionsToFlat(object obj, string prefix, Dictionary<string, string> result)
    {
        if (obj == null) return;
        var type = obj.GetType();

        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            // 跳过只有 setter 没有 getter 的属性（别名属性）
            if (!prop.CanRead) continue;
            // 跳过 nullable setter-only 别名（getter 返回 null 的可空别名属性）
            if (prop.PropertyType.IsGenericType &&
                prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                !prop.CanWrite) continue;

            // 跳过 set-only 别名属性（只有 set 没有真实 get）
            var getter = prop.GetGetMethod();
            if (getter == null) continue;

            object? value;
            try { value = prop.GetValue(obj); }
            catch { continue; }

            // 通过 ConfigurationKeyName 获取配置键名
            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;

            // 跳过别名属性（属性类型是 Nullable，且名称以 Alias 结尾）
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            if (value == null)
            {
                continue;
            }
            else if (value is string s)
            {
                result[fullKey] = s;
            }
            else if (value is bool b)
            {
                result[fullKey] = b ? "true" : "false";
            }
            else if (value is int or long or float or double or decimal)
            {
                result[fullKey] = value.ToString() ?? "";
            }
            else if (value is System.Collections.IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item is string || item?.GetType().IsPrimitive == true)
                    {
                        result[$"{fullKey}:{i}"] = item?.ToString() ?? "";
                    }
                    else if (item != null)
                    {
                        SerializeOptionsToFlat(item, $"{fullKey}:{i}", result);
                    }
                }
            }
            else if (value is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var entryKey = entry.Key?.ToString() ?? "";
                    var entryVal = entry.Value;
                    if (entryVal is string || entryVal?.GetType().IsPrimitive == true)
                    {
                        result[$"{fullKey}:{entryKey}"] = entryVal?.ToString() ?? "";
                    }
                    else if (entryVal != null)
                    {
                        SerializeOptionsToFlat(entryVal, $"{fullKey}:{entryKey}", result);
                    }
                }
            }
            else if (value.GetType().IsClass)
            {
                SerializeOptionsToFlat(value, fullKey, result);
            }
            else
            {
                result[fullKey] = value.ToString() ?? "";
            }
        }
    }

    /// <summary>
    /// 从 Options 类型提取 [Description] 属性标签，用于前端显示中文名称
    /// </summary>
    private static Dictionary<string, string> ExtractPropertyLabels(Type type, string prefix = "")
    {
        var labels = new Dictionary<string, string>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            // 跳过别名属性
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            var descAttr = prop.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .FirstOrDefault() as System.ComponentModel.DescriptionAttribute;
            if (descAttr != null && !string.IsNullOrEmpty(descAttr.Description))
            {
                labels[fullKey] = descAttr.Description;
            }

            // 递归处理嵌套对象（非集合、非基元、非字符串）
            var propType = prop.PropertyType;
            if (propType.IsClass && propType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propType)
                && !propType.IsGenericType)
            {
                foreach (var kv in ExtractPropertyLabels(propType, fullKey))
                    labels[kv.Key] = kv.Value;
            }

            // List<T> 中 T 是带 [Description] 的对象类型时，提取 T 的属性标签
            if (propType.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(propType))
            {
                var elementType = propType.GetGenericArguments()[0];
                if (elementType.IsClass && elementType != typeof(string))
                {
                    foreach (var kv in ExtractPropertyLabels(elementType, fullKey))
                        labels[kv.Key] = kv.Value;
                }
            }
        }
        return labels;
    }

    /// <summary>
    /// 提取 List&lt;T&gt; 元素类型的属性默认值
    /// 例如 List&lt;MatchRule&gt; → { "MatchRules:LogHeaders": "false", "MatchRules:LogDuration": "true", ... }
    /// 用于前端推断字段类型（bool/string/int）和初始值
    /// </summary>
    private static Dictionary<string, string> ExtractListElementDefaults(Type type, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            if (prop.Name.EndsWith("Alias", StringComparison.Ordinal)) continue;

            var keyAttr = prop.GetCustomAttributes(typeof(Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute), false)
                .FirstOrDefault() as Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute;
            var key = keyAttr?.Name ?? prop.Name;
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

            var propType = prop.PropertyType;

            // List<T> where T is a complex class
            if (propType.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(propType))
            {
                var elementType = propType.GetGenericArguments()[0];
                if (elementType.IsClass && elementType != typeof(string))
                {
                    // 创建一个 T 实例，序列化其默认值
                    try
                    {
                        var defaultInstance = Activator.CreateInstance(elementType);
                        if (defaultInstance != null)
                        {
                            SerializeOptionsToFlat(defaultInstance, fullKey, result);
                        }
                    }
                    catch { /* 无法创建实例则跳过 */ }
                }
            }

            // 递归处理嵌套对象
            if (propType.IsClass && propType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(propType)
                && !propType.IsGenericType)
            {
                foreach (var kv in ExtractListElementDefaults(propType, fullKey))
                    result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// 将 flat key-value (如 "MaxWidth" → "2048", "SupportedFormats:0" → "jpg") 转为嵌套字典
    /// </summary>
    private static Dictionary<string, object> FlatKeysToNestedDict(Dictionary<string, string> flat)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in flat)
        {
            var parts = key.Split(':');
            SetNestedValue(result, parts, 0, value);
        }

        // 将纯数字 key 的字典转为数组
        return ConvertIndexedDictsToArrays(result);
    }

    private static void SetNestedValue(Dictionary<string, object> dict, string[] parts, int index, string value)
    {
        if (index == parts.Length - 1)
        {
            dict[parts[index]] = value;
            return;
        }

        var key = parts[index];
        if (!dict.TryGetValue(key, out var existing) || existing is not Dictionary<string, object> nested)
        {
            nested = new Dictionary<string, object>();
            dict[key] = nested;
        }
        SetNestedValue(nested, parts, index + 1, value);
    }

    /// <summary>
    /// 如果一个字典的所有 key 都是连续数字 (0, 1, 2, ...)，则转成 List
    /// </summary>
    private static Dictionary<string, object> ConvertIndexedDictsToArrays(Dictionary<string, object> dict)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, val) in dict)
        {
            if (val is Dictionary<string, object> nested)
            {
                var converted = ConvertIndexedDictsToArrays(nested);
                // 检查是否所有 key 是数字索引
                if (converted.Count > 0 && converted.Keys.All(k => int.TryParse(k, out _)))
                {
                    var list = converted.OrderBy(kv => int.Parse(kv.Key)).Select(kv => kv.Value).ToList();
                    result[key] = list;
                }
                else
                {
                    result[key] = converted;
                }
            }
            else
            {
                result[key] = val;
            }
        }
        return result;
    }

    /// <summary>
    /// 修改补丁文件中的 PluginConfigs 节点
    /// </summary>
    private static async Task<IResult> ModifyPluginConfigPatch(IConfiguration config, string pluginId,
        Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var pluginConfigs = EnsurePatchSection(patch, "PluginConfigs");

            var error = modifier(pluginConfigs);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "插件配置已更新" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    // =============== 配置来源检测辅助方法 ===============

    /// <summary>
    /// 配置来源枚举
    /// </summary>
    private enum ConfigSource
    {
        OriginalConfig,  // 原始配置文件 (.ly 或 .yaml)
        PatchConfig,     // 补丁配置文件 (.lywaf.lb-patch.json)
        NotFound         // 配置不存在
    }

    /// <summary>
    /// 检测指定配置项的来源
    /// </summary>
    /// <param name="config">配置对象</param>
    /// <param name="key">配置键</param>
    /// <returns>配置来源</returns>
    private static ConfigSource DetectConfigSource(IConfiguration config, string key)
    {
        // 检查是否存在于补丁配置中
        var patchPath = LbPatchConfig.GetPatchFilePath();
        if (File.Exists(patchPath))
        {
            try
            {
                var json = File.ReadAllText(patchPath, Encoding.UTF8);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                
                // 将配置键转换为补丁文件中的路径
                var patchKey = key;
                if (patchKey.StartsWith("ReverseProxy:"))
                {
                    patchKey = patchKey.Substring("ReverseProxy:".Length);
                }
                else if (patchKey.StartsWith("WafInfos:Listens:"))
                {
                    patchKey = patchKey.Substring("WafInfos:".Length);
                }
                
                if (FindValueInJsonElement(doc.RootElement, patchKey))
                {
                    return ConfigSource.PatchConfig;
                }
            }
            catch (Exception)
            {
                // 补丁文件读取失败，继续检查原始配置
            }
        }
        
        // 检查是否存在于原始配置中
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath, Encoding.UTF8);
                
                if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    // YAML 文件解析
                    using var reader = new StringReader(content);
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build();
                    var yamlObj = deserializer.Deserialize(reader);
                    if (yamlObj != null && FindValueInYamlObject(yamlObj, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
                else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    // JSON 文件解析
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    if (FindValueInJsonElement(doc.RootElement, key))
                    {
                        return ConfigSource.OriginalConfig;
                    }
                }
            }
            catch (Exception)
            {
                // 原始配置文件读取失败
            }
        }
        
        return ConfigSource.NotFound;
    }

    /// <summary>
    /// 在 JsonElement 中查找指定的键路径
    /// </summary>
    private static bool FindValueInJsonElement(System.Text.Json.JsonElement element, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = element;
        
        foreach (var part in parts)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
                
            if (!current.TryGetProperty(part, out current))
                return false;
        }
        
        return true;
    }

    /// <summary>
    /// 在 YAML 对象中查找指定的键路径
    /// </summary>
    private static bool FindValueInYamlObject(object yamlObj, string keyPath)
    {
        var parts = keyPath.Split(':');
        var current = yamlObj;
        
        foreach (var part in parts)
        {
            if (current is not Dictionary<object, object> dict)
                return false;
                
            if (!dict.TryGetValue(part, out current))
                return false;
        }
        
        return current != null;
    }

    // =============== 补丁文件辅助方法 ===============

    private static readonly SemaphoreSlim _lbPatchLock = new(1, 1);

    /// <summary>
    /// 读取补丁文件并反序列化为可操作的字典
    /// </summary>
    private static async Task<Dictionary<string, object>> ReadPatchFile()
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        Dictionary<string, object> patch;
        if (File.Exists(patchPath))
        {
            var json = await File.ReadAllTextAsync(patchPath, Encoding.UTF8);
            try
            {
                patch = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }) ?? new();
            }
            catch (System.Exception)
            {
                patch = new Dictionary<string, object>();
            }
            }
        else
        {
            patch = new Dictionary<string, object>();
        }
        return patch;
    }

    /// <summary>
    /// 确保补丁中指定节点存在，并返回可操作的字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchSection(Dictionary<string, object> patch, string sectionName)
    {
        if (!patch.ContainsKey(sectionName))
            patch[sectionName] = new Dictionary<string, object>();

        var sectionObj = patch[sectionName];
        Dictionary<string, object> section;
        if (sectionObj is System.Text.Json.JsonElement je)
        {
            section = JsonElementToDict(je);
        }
        else if (sectionObj is Dictionary<string, object> dict)
        {
            section = dict;
        }
        else
        {
            section = new Dictionary<string, object>();
        }
        patch[sectionName] = section;
        return section;
    }

    /// <summary>
    /// 更新补丁中的源文件跟踪信息
    /// </summary>
    private static void UpdatePatchSourceTracking(Dictionary<string, object> patch)
    {
        var configPath = SharedData.ConfigFilePath;
        if (File.Exists(configPath))
        {
            var lastModified = File.GetLastWriteTimeUtc(configPath);
            patch["_source"] = new Dictionary<string, object>
            {
                ["file"] = Path.GetFileName(configPath),
                ["lastModified"] = lastModified.ToString("o")
            };
        }
    }

    /// <summary>
    /// 保存补丁文件并触发配置重载
    /// </summary>
    private static async Task SavePatchAndReload(Dictionary<string, object> patch, IConfiguration config)
    {
        var patchPath = LbPatchConfig.GetPatchFilePath();
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var newJson = System.Text.Json.JsonSerializer.Serialize(patch, options);
        await File.WriteAllTextAsync(patchPath, newJson, Encoding.UTF8);

        if (config is IConfigurationRoot configRoot)
        {
            configRoot.Reload();
        }
    }

    /// <summary>
    /// 修改集群补丁并触发配置重载
    /// modifier 接收 Clusters 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyLbPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var clusters = EnsurePatchSection(patch, "Clusters");

            var error = modifier(clusters);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改路由补丁并触发配置重载
    /// modifier 接收 Routes 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyRoutePatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var routes = EnsurePatchSection(patch, "Routes");

            var error = modifier(routes);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 FileServer 节点
    /// modifier 接收 FileServer 字典，返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyFileServerPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var fileservers = EnsurePatchSection(patch, "FileServer");

            var error = modifier(fileservers);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 SimpleRes 节点
    /// </summary>
    private static async Task<IResult> ModifySimpleResPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var simpleres = EnsurePatchSection(patch, "SimpleRes");

            var error = modifier(simpleres);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改补丁文件中的 Listens 节点（监听端口）
    /// modifier 接收 Listens 字典（key 为索引），返回 null 表示成功，返回字符串表示错误
    /// </summary>
    private static async Task<IResult> ModifyListenPatch(IConfiguration config, Func<Dictionary<string, object>, string?> modifier,
        bool restartRequired = true)
    {
        await _lbPatchLock.WaitAsync();
        try
        {
            var patch = await ReadPatchFile();
            var listens = EnsurePatchSection(patch, "Listens");

            var error = modifier(listens);
            if (error != null)
            {
                return Results.Json(new { success = false, message = error }, statusCode: 400);
            }

            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);

            if (restartRequired)
            {
                return Results.Json(new { success = true, message = "监听端口已更新，需要重启服务才能生效", restartRequired = true });
            }
            return Results.Json(new { success = true, message = "配置已更新并重载" });
        }
        finally
        {
            _lbPatchLock.Release();
        }
    }

    /// <summary>
    /// 修改原始配置文件并触发配置重载
    /// modifier 返回 null 表示成功，返回字符串表示错误信息
    /// </summary>
    private static async Task<IResult> ModifyOriginalConfig(IConfiguration config, string clusterId, Func<object, string?> modifier)
    {
        var configPath = SharedData.ConfigFilePath;
        if (!File.Exists(configPath))
        {
            return Results.Json(new { success = false, message = "配置文件不存在" }, statusCode: 500);
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            object configObj;
            
            if (configPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                configPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                // YAML 文件处理
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                var serializer = new SerializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
                    .Build();
                    
                using var reader = new StringReader(content);
                configObj = deserializer.Deserialize(reader) ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 YAML 文件
                var newYaml = serializer.Serialize(configObj);
                await File.WriteAllTextAsync(configPath, newYaml, Encoding.UTF8);
            }
            else if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                // JSON 文件处理
                var jsonOptions = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = false 
                };
                
                configObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(content) 
                           ?? new Dictionary<string, object>();
                
                // 执行修改
                var error = modifier(configObj);
                if (error != null)
                {
                    return Results.Json(new { success = false, message = error }, statusCode: 400);
                }
                
                // 保存 JSON 文件
                var newJson = System.Text.Json.JsonSerializer.Serialize(configObj, jsonOptions);
                await File.WriteAllTextAsync(configPath, newJson, Encoding.UTF8);
            }
            else
            {
                return Results.Json(new { success = false, message = "不支持的配置文件格式" }, statusCode: 500);
            }

            // 触发配置重载
            if (config is IConfigurationRoot configRoot)
            {
                configRoot.Reload();
            }

            return Results.Json(new { success = true, message = "原始配置已更新并重载" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { success = false, message = $"修改原始配置失败: {ex.Message}" }, statusCode: 500);
        }
    }

    /// <summary>
    /// 确保补丁中存在指定集群节点，返回该集群字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchCluster(Dictionary<string, object> clusters, string clusterId)
    {
        if (clusters.TryGetValue(clusterId, out var clusterObj))
        {
            if (clusterObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusters[clusterId] = dict;
                return dict;
            }
            if (clusterObj is Dictionary<string, object> existing)
                return existing;
        }
        var newCluster = new Dictionary<string, object>();
        clusters[clusterId] = newCluster;
        return newCluster;
    }

    /// <summary>
    /// 确保补丁集群字典中存在 Destinations 子字典，不存在则创建空字典
    /// </summary>
    private static Dictionary<string, object> EnsurePatchDestinations(Dictionary<string, object> clusterPatch)
    {
        if (clusterPatch.TryGetValue("Destinations", out var destsObj))
        {
            if (destsObj is System.Text.Json.JsonElement je)
            {
                var dict = JsonElementToDict(je);
                clusterPatch["Destinations"] = dict;
                return dict;
            }
            if (destsObj is Dictionary<string, object> existing)
                return existing;
        }
        var newDests = new Dictionary<string, object>();
        clusterPatch["Destinations"] = newDests;
        return newDests;
    }

    /// <summary>
    /// [已废弃] 从 IConfiguration 快照当前集群的 Destinations 和 LoadBalancingPolicy 到补丁字典
    /// </summary>
    private static void SnapshotClusterDestinations(IConfiguration config, string clusterId, Dictionary<string, object> clusterPatch)
    {
        var clusterSection = config.GetSection($"ReverseProxy:Clusters:{clusterId}");

        // 保持策略
        if (!clusterPatch.ContainsKey("LoadBalancingPolicy"))
        {
            var policy = clusterSection["LoadBalancingPolicy"];
            if (policy != null) clusterPatch["LoadBalancingPolicy"] = policy;
        }

        // 快照 destinations（如果补丁中还没有）
        if (!clusterPatch.ContainsKey("Destinations"))
        {
            var dests = new Dictionary<string, object>();
            var destsSection = clusterSection.GetSection("Destinations");
            foreach (var dest in destsSection.GetChildren())
            {
                var destDict = new Dictionary<string, object>();
                var address = dest["Address"];
                if (address != null) destDict["Address"] = address;

                var metaSection = dest.GetSection("Metadata");
                var metaChildren = metaSection.GetChildren().ToList();
                if (metaChildren.Count > 0)
                {
                    var meta = new Dictionary<string, object>();
                    foreach (var m in metaChildren)
                    {
                        if (m.Value != null) meta[m.Key] = m.Value;
                    }
                    destDict["Metadata"] = meta;
                }

                dests[dest.Key] = destDict;
            }
            clusterPatch["Destinations"] = dests;
        }
    }

    /// <summary>
    /// 递归将 JsonElement 转换为 Dictionary
    /// </summary>
    private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToValue(prop.Value);
        }
        return dict;
    }

    private static object JsonElementToValue(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => JsonElementToDict(value),
            System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToValue).ToList<object>(),
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => value.ToString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    /// <summary>
    /// 将 JsonElement 转换为 YAML 兼容的字典（保留原生类型：bool/int/string/List/Dict）
    /// </summary>
    private static Dictionary<string, object> JsonElementToYamlDict(System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, object>();
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = JsonElementToYamlValue(prop.Value);
        }
        return dict;
    }

    private static object JsonElementToYamlValue(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => JsonElementToYamlDict(value),
            System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Select(JsonElementToYamlValue).ToList(),
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => value.TryGetInt32(out var i) ? i : (object)value.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => value.ToString()
        };
    }

    /// <summary>
    /// 确保字典中存在指定 key 的子字典，不存在则创建
    /// </summary>
    private static Dictionary<string, object> EnsureNestedDict(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var existing) && existing is Dictionary<string, object> dict)
            return dict;

        var newDict = new Dictionary<string, object>();
        parent[key] = newDict;
        return newDict;
    }

    /// <summary>
    /// 从路由 ID 中提取端口号，支持 listen_{port}_default / simpleres_listen_{port}_default 等格式
    /// </summary>
    private static int? ExtractPortFromRouteId(string routeId)
    {
        var listenIdx = routeId.IndexOf("listen_", StringComparison.Ordinal);
        if (listenIdx < 0 || !routeId.EndsWith("_default")) return null;
        var portStr = routeId[(listenIdx + "listen_".Length)..^"_default".Length];
        return int.TryParse(portStr, out var port) ? port : null;
    }

    private static object? GetSectionValue(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        
        if (children.Count == 0)
        {
            return section.Value;
        }
        
        // 检查是否是数组（子项的 Key 都是数字）
        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            var list = new List<object?>();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
            {
                list.Add(GetSectionValue(child));
            }
            return list;
        }
        
        // 否则是对象
        var dict = new Dictionary<string, object?>();
        foreach (var child in children)
        {
            dict[child.Key] = GetSectionValue(child);
        }
        return dict;
    }

    // =============== WAF 自定义规则辅助方法 ===============

    private static string GetFieldLabel(WafMatchField field) => field switch
    {
        WafMatchField.UriPath => "URL 路径",
        WafMatchField.FullUrl => "完整 URL",
        WafMatchField.QueryString => "查询字符串",
        WafMatchField.Method => "HTTP 方法",
        WafMatchField.ClientIp => "客户端 IP",
        WafMatchField.XForwardedFor => "X-Forwarded-For",
        WafMatchField.UserAgent => "User-Agent",
        WafMatchField.Referer => "Referer",
        WafMatchField.ContentType => "Content-Type",
        WafMatchField.ContentLength => "Content-Length",
        WafMatchField.Cookie => "Cookie",
        WafMatchField.Header => "请求头",
        WafMatchField.QueryParam => "查询参数",
        WafMatchField.Body => "请求体",
        WafMatchField.ServerPort => "服务端口",
        _ => field.ToString()
    };

    private static string GetOperatorLabel(WafMatchOperator op) => op switch
    {
        WafMatchOperator.Equal => "等于",
        WafMatchOperator.NotEqual => "不等于",
        WafMatchOperator.Contains => "包含",
        WafMatchOperator.NotContains => "不包含",
        WafMatchOperator.StartsWith => "前缀匹配",
        WafMatchOperator.EndsWith => "后缀匹配",
        WafMatchOperator.Regex => "正则匹配",
        WafMatchOperator.Exists => "存在",
        WafMatchOperator.NotExists => "不存在",
        WafMatchOperator.LengthGreaterThan => "长度大于",
        WafMatchOperator.LengthLessThan => "长度小于",
        _ => op.ToString()
    };

    private static string GetActionLabel(WafRuleAction action) => action switch
    {
        WafRuleAction.Observe => "观察",
        WafRuleAction.Block => "封禁 IP",
        WafRuleAction.Reject => "拦截",
        WafRuleAction.Captcha => "人机验证",
        _ => action.ToString()
    };

    private static string GetSourceLabel(WafRuleSource source) => source switch
    {
        WafRuleSource.User => "用户规则",
        WafRuleSource.Config => "配置规则",
        WafRuleSource.System => "系统规则",
        _ => source.ToString()
    };

    /// <summary>
    /// 获取本机真实局域网 IP 地址
    /// 排除虚拟网卡（VMware、VirtualBox、Hyper-V、Docker 等）
    /// 优先选择有默认网关的物理/Wi-Fi 网卡上的 RFC 1918 私有地址
    /// </summary>
    private static string GetLocalNetworkIp(GlobalOptions? globalOptions = null)
    {
        // 优先使用用户配置的本地地址
        if (!string.IsNullOrWhiteSpace(globalOptions?.LocalAddress))
            return globalOptions.LocalAddress.Trim();

        try
        {
            var candidates = new List<(System.Net.IPAddress addr, int priority, bool hasGateway)>();
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in networkInterfaces)
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                // 排除虚拟网卡
                if (IsVirtualAdapter(ni)) continue;

                // 只保留以太网和 Wi-Fi
                if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                    && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet)
                    continue;

                var ipProps = ni.GetIPProperties();

                // 有默认网关 = 真正联网的接口
                var hasGateway = ipProps.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !g.Address.Equals(System.Net.IPAddress.Any));

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                    var bytes = addr.GetAddressBytes();
                    var priority = GetPrivateAddressPriority(bytes);
                    if (priority >= 0)
                        candidates.Add((addr, priority, hasGateway));
                }
            }

            if (candidates.Count > 0)
            {
                // 有网关的优先，然后按私有地址优先级排序
                candidates.Sort((a, b) =>
                {
                    var gwCmp = b.hasGateway.CompareTo(a.hasGateway); // true 排前面
                    return gwCmp != 0 ? gwCmp : a.priority.CompareTo(b.priority);
                });
                return candidates[0].addr.ToString();
            }

            // 没有匹配的私有地址，用 UDP socket 探测出口 IP
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }

        return "127.0.0.1";
    }

    /// <summary>
    /// 判断网卡是否为虚拟网卡（VMware、VirtualBox、Hyper-V、Docker、WSL 等）
    /// 通过 Description 和 Name 中的关键字匹配
    /// </summary>
    private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface ni)
    {
        var desc = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        string[] virtualKeywords = [
            "vmware", "vmnet", "virtualbox", "vbox",
            "hyper-v", "virtual",
            "docker", "veth", "br-",
            "wsl", "vpn", "tap-", "tun",
            "teredo", "isatap", "6to4",
            "bluetooth", "vnic", "virbr"
        ];

        foreach (var kw in virtualKeywords)
        {
            if (desc.Contains(kw) || name.Contains(kw))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 判断 IPv4 是否为 RFC 1918 私有地址，并返回优先级
    /// 192.168.x.x = 0（最高）, 10.x.x.x = 1, 172.16-31.x.x = 2
    /// 非私有地址返回 -1
    /// </summary>
    private static int GetPrivateAddressPriority(byte[] bytes)
    {
        if (bytes[0] == 192 && bytes[1] == 168) return 0;
        if (bytes[0] == 10) return 1;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 2;
        return -1;
    }

}

// =============== 请求模型 ===============

public class AddIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class RemoveIpRequest
{
    public string IpOrCidr { get; set; } = "";
}

public class BlockIpRequest
{
    public string Ip { get; set; } = "";
    public string? Reason { get; set; }
    public TimeSpan? Duration { get; set; }
    /// <summary>
    /// 限制类型: blocked(封禁), captcha(验证码), throttled(限速), log(日志记录)
    /// </summary>
    public string? Type { get; set; }
    /// <summary>
    /// 限速速度（KB/s），仅 type=throttled 时有效
    /// </summary>
    public int SpeedLimit { get; set; }
}

public class UnblockIpRequest
{
    public string Ip { get; set; } = "";
}

public class AddWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class RemoveWafRuleRequest
{
    public string Pattern { get; set; } = "";
}

public class AddCcRuleRequest
{
    public string Path { get; set; } = "";
    public int? Period { get; set; }
    public int? LimitNum { get; set; }
    public TimeSpan? FbTime { get; set; }
}

public class RemoveCcRuleRequest
{
    public string Path { get; set; } = "";
}

// =============== 高级 CC 规则请求模型 ===============

public class CcConditionRequest
{
    public string Target { get; set; } = "UrlPath";
    public string Operator { get; set; } = "Equal";
    public List<string>? Values { get; set; }
}

public class AddAdvancedCcRuleRequest
{
    public string Name { get; set; } = "";
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class UpdateAdvancedCcRuleRequest
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public List<CcConditionRequest>? Conditions { get; set; }
    public int? Period { get; set; }
    public int? Threshold { get; set; }
    public string? Action { get; set; }
    public int? ActionSeconds { get; set; }
    public int? Priority { get; set; }
}

public class RemoveAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
}

public class ToggleAdvancedCcRuleRequest
{
    public string RuleId { get; set; } = "";
    public bool? Enabled { get; set; }
}

public class AddCountryRequest
{
    public string Country { get; set; } = "";
}

public class RemoveCountryRequest
{
    public string Country { get; set; } = "";
}

public class AddRegionRequest
{
    public string Region { get; set; } = "";
}

public class RemoveRegionRequest
{
    public string Region { get; set; } = "";
}

public class AddIpThrottleRequest
{
    public string Ip { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemoveIpThrottleRequest
{
    public string Ip { get; set; } = "";
}

public class AddPathThrottleRequest
{
    public string Path { get; set; } = "";
    public int LimitKbps { get; set; }
}

public class RemovePathThrottleRequest
{
    public string Path { get; set; } = "";
}

public class SetGlobalThrottleRequest
{
    public int LimitKbps { get; set; }
}

public class UpdateConnectionLimitRequest
{
    public int? MaxConnectionsPerIp { get; set; }
    public int? MaxConnectionsPerDestination { get; set; }
    public int? MaxTotalConnections { get; set; }
    public int? RejectStatusCode { get; set; }
}

public class PathConnectionLimitRequest
{
    public string Path { get; set; } = "";
    public int MaxConnections { get; set; }
}

public class RemovePathConnectionLimitRequest
{
    public string Path { get; set; } = "";
}

public class CreateABTestRequest
{
    public string TestId { get; set; } = "";
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Mode { get; set; } // Random, CookieSticky, IpHash, UserIdHash
    public string? CookieName { get; set; }
    public int? CookieExpireDays { get; set; }
    public Dictionary<string, int> Variants { get; set; } = new(); // { "A": 70, "B": 30 }
    public Dictionary<string, string>? VariantTargets { get; set; } // { "A": "destination-a", "B": "destination-b" }
    public List<string>? MatchPaths { get; set; }
    public List<string>? ExcludePaths { get; set; }
}

public class ToggleABTestRequest
{
    public bool? Enabled { get; set; }
}

public class ToggleFeatureRequest
{
    public bool? Enabled { get; set; }
}

public class SaveConfigRequest
{
    public string Content { get; set; } = "";
    public bool Reload { get; set; }
}

public class ConvertConfigRequest
{
    public string Content { get; set; } = "";
}

public class ParamUpdateRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("isFirstServer")]
    public bool? IsFirstServer { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("localAddress")]
    public string? LocalAddress { get; set; }
}

// =============== IP 请求日志请求模型 ===============

public class IpLogRequest
{
    public string Ip { get; set; } = "";
    public TimeSpan? Duration { get; set; }
}

// =============== 负载均衡请求模型 ===============

public class UpdateClusterPolicyRequest
{
    public string ClusterId { get; set; } = "";
    public string Policy { get; set; } = "";
}

public class AddDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string Address { get; set; } = "";
    public Dictionary<string, string>? Metadata { get; set; }
}

public class UpdateDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string? Address { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class RemoveDestinationRequest
{
    public string ClusterId { get; set; } = "";
    public string DestinationId { get; set; } = "";
}

public class BatchRemoveDestinationsRequest
{
    public string ClusterId { get; set; } = "";
    public List<string> DestinationIds { get; set; } = [];
}

public class RemoveClusterPatchRequest
{
    public string ClusterId { get; set; } = "";
}

public class UpdateRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class UpdateRouteMatchRequest
{
    public string? Path { get; set; }
    public List<string>? Hosts { get; set; }
    public List<string>? Methods { get; set; }
}

public class AddRouteRequest
{
    public string RouteId { get; set; } = "";
    public string? ClusterId { get; set; }
    public int? Order { get; set; }
    public UpdateRouteMatchRequest? Match { get; set; }
}

public class RemoveRoutePatchRequest
{
    public string RouteId { get; set; } = "";
}

public class AddFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class UpdateFileServerRequest
{
    public string ItemId { get; set; } = "";
    public string? Prefix { get; set; }
    public string? BasePath { get; set; }
    public bool? Browse { get; set; }
    public bool? PreCompressed { get; set; }
    public long? MaxFileSize { get; set; }
    public List<string>? TryFiles { get; set; }
    public List<string>? DefaultFiles { get; set; }
}

public class RemoveFileServerRequest
{
    public string ItemId { get; set; } = "";
}

public class AddSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class UpdateSimpleResRequest
{
    public string ItemId { get; set; } = "";
    public string? Body { get; set; }
    public string? ContentType { get; set; }
    public int? StatusCode { get; set; }
    public string? Charset { get; set; }
    public bool? ShowReq { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public class RemoveSimpleResRequest
{
    public string ItemId { get; set; } = "";
}

public class PluginToggleRequest
{
    public string PluginId { get; set; } = "";
}

public class PluginConfigUpdateRequest
{
    public string PluginId { get; set; } = "";
    public Dictionary<string, string> Config { get; set; } = new();
}

public class LoginRequest
{
    public string Username { get; set; } = "";
    /// <summary>SHA256(SHA256(password) + timestamp)</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>客户端时间戳（Unix 秒，经服务器时间校准）</summary>
    public long Timestamp { get; set; }
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public class CertUploadRequest
{
    public string Domain { get; set; } = "";
    public string PemContent { get; set; } = "";
    public string KeyContent { get; set; } = "";
}

public class CertDeleteRequest
{
    public string PemFile { get; set; } = "";
}

public class AcmeApplyRequest
{
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
}

public class AcmeRenewRequest
{
    public string Domain { get; set; } = "";
    public string Email { get; set; } = "";
}

public class PathRequest
{
    public string Path { get; set; } = "";
}

public class PcapToggleRequest
{
    public string PortKey { get; set; } = "";
    public bool Enabled { get; set; }
}
