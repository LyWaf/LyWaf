namespace LyWaf.Services.Param;

public class GlobalOptions
{
    /// <summary>
    /// 当前是否为首台服务器（直接面对客户端）
    /// 为 true 时，GetClientIp 无条件取连接 IP（RemoteIpAddress），忽略代理头
    /// 为 false 时，优先读取 X-Forwarded-For / X-Real-IP 等代理头
    /// </summary>
    public bool IsFirstServer { get; set; } = false;

    /// <summary>
    /// 本机地址（用于 CRL 分发点等场景）
    /// 为空时自动检测本机局域网 IP
    /// </summary>
    public string? LocalAddress { get; set; }

    /// <summary>
    /// 管理员邮箱（用于 ACME 证书申请、告警通知等）
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// 是否启用调试模式
    /// 为 true 时日志级别设为 Debug，输出更详细的调试信息
    /// </summary>
    public bool Debug { get; set; } = false;
}
