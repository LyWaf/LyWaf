namespace LyWaf.Services.Param;

public class ParamOptions
{
    /// <summary>
    /// 当前是否为首台服务器（直接面对客户端）
    /// 为 true 时，GetClientIp 无条件取连接 IP（RemoteIpAddress），忽略代理头
    /// 为 false 时，优先读取 X-Forwarded-For / X-Real-IP 等代理头
    /// </summary>
    public bool IsFirstServer { get; set; } = false;
}
