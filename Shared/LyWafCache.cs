namespace LyWaf.Shared;

/// <summary>
/// HttpContext.Items 共享缓存 Key
/// 用于中间件/插件之间共享已捕获的请求体/响应体，避免重复读取流
/// </summary>
public static class LyWafCache
{
    /// <summary>已捕获的请求体字符串</summary>
    public const string RequestBody = "LyWafCache.RequestBody";

    /// <summary>已捕获的响应体字符串</summary>
    public const string ResponseBody = "LyWafCache.ResponseBody";
}
