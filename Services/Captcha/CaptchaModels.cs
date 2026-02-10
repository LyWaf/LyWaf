namespace LyWaf.Services.Captcha;

/// <summary>
/// 验证码待验证信息
/// </summary>
public class CaptchaPendingInfo
{
    /// <summary>
    /// 触发的规则名称
    /// </summary>
    public string RuleName { get; set; } = "";

    /// <summary>
    /// 处罚持续时间（秒）
    /// </summary>
    public int ActionSeconds { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
