using System.Text;
using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using Microsoft.Extensions.FileProviders;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapFrontendEndpoints(WebApplication app, int controlPort)
    {
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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();
        
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
                var root = Path.GetFullPath(distDir) + Path.DirectorySeparatorChar;
                var filePath = Path.GetFullPath(Path.Combine(distDir, path));
                if (!filePath.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return Results.NotFound();
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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
