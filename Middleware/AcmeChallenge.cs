using LyWaf.Services.Acme;
using NLog;

namespace LyWaf.Middleware;

/// <summary>
/// ACME HTTP-01 挑战中间件
/// 处理 Let's Encrypt 的域名验证请求
/// 路径格式: /.well-known/acme-challenge/{token}
/// </summary>
public class AcmeChallengeMiddleware
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next;
    private const string ChallengePath = "/.well-known/acme-challenge/";

    public AcmeChallengeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAcmeService acmeService)
    {
        var path = context.Request.Path.Value;

        // 检查是否是 ACME 挑战请求
        if (path != null && path.StartsWith(ChallengePath, StringComparison.OrdinalIgnoreCase))
        {
            var token = path[ChallengePath.Length..];
            
            if (!string.IsNullOrEmpty(token))
            {
                var response = acmeService.GetChallengeResponse(token);
                
                if (response != null)
                {
                    _logger.Debug("处理 ACME 挑战请求: token={Token}", token);
                    
                    context.Response.ContentType = "text/plain";
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync(response);
                    return;
                }
                else
                {
                    _logger.Warn("ACME 挑战 token 未找到: {Token}", token);
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Challenge not found");
                    return;
                }
            }
        }

        await _next(context);
    }
}

/// <summary>
/// ACME 中间件扩展方法
/// </summary>
public static class AcmeChallengeMiddlewareExtensions
{
    /// <summary>
    /// 使用 ACME HTTP-01 挑战中间件
    /// </summary>
    public static IApplicationBuilder UseAcmeChallenge(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AcmeChallengeMiddleware>();
    }
}
