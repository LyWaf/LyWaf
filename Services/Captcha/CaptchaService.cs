using System.Collections.Concurrent;
using System.Security.Cryptography;
using LyWaf.Services.Statistic;
using LyWaf.Shared;
using LyWaf.Services.Metrics;
using Microsoft.Extensions.DependencyInjection;
using LyWaf.Services.RuntimeState;
using NLog;
using LyWaf.Localization;

namespace LyWaf.Services.Captcha;

/// <summary>
/// 验证码服务接口
/// </summary>
public interface ICaptchaService
{
    /// <summary>
    /// 为指定 IP 生成验证码挑战
    /// </summary>
    CaptchaChallenge GenerateChallenge(string clientIp);
    Task<CaptchaChallenge> GenerateChallengeAsync(string clientIp, CancellationToken cancellationToken = default)
        => Task.FromResult(GenerateChallenge(clientIp));

    /// <summary>
    /// 验证验证码答案
    /// </summary>
    CaptchaVerifyResult Verify(string clientIp, string token, string answer, BehaviorData? behavior = null);
    Task<CaptchaVerifyResult> VerifyAsync(string clientIp, string token, string answer, BehaviorData? behavior,
        CcCaptchaTarget? target, CancellationToken cancellationToken = default)
        => Task.FromResult(Verify(clientIp, token, answer, behavior));

    /// <summary>
    /// 将 IP 标记为需要验证码
    /// </summary>
    void SetPending(string clientIp, string ruleName, int actionSeconds);

    /// <summary>
    /// 检查 IP 是否需要验证码
    /// </summary>
    bool IsPending(string clientIp);

    /// <summary>
    /// 解除 IP 的验证码要求（验证通过后）
    /// </summary>
    void RemovePending(string clientIp);
}

/// <summary>
/// 验证码挑战
/// </summary>
public class CaptchaChallenge
{
    /// <summary>
    /// 验证码令牌（用于后续验证）
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// 验证模式: "math" = 算术+鼠标, "slider" = 滑块+鼠标
    /// </summary>
    public string Mode { get; set; } = "math";

    /// <summary>
    /// 数学题图片 base64（仅 math 模式）
    /// </summary>
    public string? QuestionImage { get; set; }

    /// <summary>
    /// 滑块目标位置百分比（仅 slider 模式）
    /// </summary>
    public int? SliderTarget { get; set; }

    /// <summary>
    /// 滑块背景图片 base64（仅 slider 模式，含干扰元素）
    /// </summary>
    public string? SliderBackground { get; set; }
}

/// <summary>
/// 验证码验证结果
/// </summary>
public class CaptchaVerifyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public CcCaptchaReleaseConfirmation? Release { get; set; }
    public string? ErrorCode { get; set; }
    public int FailureStatus { get; set; } = 400;
}

/// <summary>
/// 行为验证数据
/// </summary>
public class BehaviorData
{
    /// <summary>
    /// 鼠标轨迹点数量
    /// </summary>
    public int MouseTrackCount { get; set; }

    /// <summary>
    /// 从页面加载到提交的耗时（毫秒）
    /// </summary>
    public long Duration { get; set; }

    /// <summary>
    /// 滑块滑动的目标偏移量
    /// </summary>
    public int SliderOffset { get; set; }
}

/// <summary>
/// 验证码服务实现
/// </summary>
public class CaptchaService : ICaptchaService
{
    private const string PendingDomain = "captcha.pending";
    private const string ChallengeDomain = "captcha.challenge";
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IServiceProvider _serviceProvider;
    private readonly PrometheusMetrics _metrics;
    private readonly IRuntimeStateStore _runtimeState;
    private readonly ISharedCaptchaStore? _sharedStore;

    // 存储验证码挑战: Key = token, Value = (正确答案, IP, 过期时间)
    private static readonly ConcurrentDictionary<string, CaptchaEntry> _challenges = new();
    // Both copies belong to one process-local consume operation, including across service instances.
    private static readonly object[] ChallengeGates = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();
    private static object ChallengeGate(string token) => ChallengeGates[(uint)StringComparer.Ordinal.GetHashCode(token) % (uint)ChallengeGates.Length];

    public CaptchaService(IServiceProvider serviceProvider, PrometheusMetrics metrics, IRuntimeStateStore runtimeState)
    {
        _serviceProvider = serviceProvider;
        _metrics = metrics;
        _runtimeState = runtimeState;
        _sharedStore = serviceProvider.GetService<ISharedCaptchaStore>();
    }

    public void SetPending(string clientIp, string ruleName, int actionSeconds)
    {
        var info = new CaptchaPendingInfo
        {
            RuleName = ruleName,
            ActionSeconds = actionSeconds
        };
        SharedData.CaptchaPending.Set(clientIp, info, TimeSpan.FromSeconds(actionSeconds));
        _runtimeState.Set(PendingDomain, clientIp, info, TimeSpan.FromSeconds(actionSeconds));
        _logger.Info("设置验证码验证: IP={Ip}, Rule={Rule}, Duration={Duration}秒", clientIp, ruleName, actionSeconds);
    }

    public bool IsPending(string clientIp)
    {
        return SharedData.CaptchaPending.TryGetValue(clientIp, out _)
            || _runtimeState.TryGet<CaptchaPendingInfo>(PendingDomain, clientIp, out _);
    }

    public void RemovePending(string clientIp)
    {
        SharedData.CaptchaPending.Remove(clientIp);
        _runtimeState.Remove(PendingDomain, clientIp);

        // 清除该 IP 的 CC 触发记录和计数器，避免验证通过后重复触发
        var ccRuleChecker = _serviceProvider.GetService<ICcRuleChecker>();
        ccRuleChecker?.ClearTriggeredRecords(clientIp);

        _logger.Info("验证码验证通过，解除限制: IP={Ip}", clientIp);
    }

    public CaptchaChallenge GenerateChallenge(string clientIp)
        => GenerateChallengeAsync(clientIp).GetAwaiter().GetResult();

    public async Task<CaptchaChallenge> GenerateChallengeAsync(string clientIp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clientIp is not { Length: > 0 and <= 2048 }) throw new ArgumentException("Invalid challenge subject");
        // 清理过期的挑战
        CleanupExpired();

        var token = (_sharedStore is null ? "" : "r1.") + GenerateToken();

        // 随机选择验证模式: math(算术+鼠标) 或 slider(滑块+鼠标)
        var mode = RandomNumberGenerator.GetInt32(0, 2) == 0 ? "math" : "slider";

        var entry = new CaptchaEntry
        {
            Mode = mode,
            ClientIp = clientIp,
            ExpiryTime = DateTime.UtcNow.AddMinutes(5)
        };

        var challenge = new CaptchaChallenge
        {
            Token = token,
            Mode = mode
        };

        if (mode == "math")
        {
            var (question, answer) = GenerateMathQuestion();
            entry.Answer = answer;
            challenge.QuestionImage = CaptchaImageGenerator.GenerateMathImage(question);
        }
        else
        {
            var sliderTarget = RandomNumberGenerator.GetInt32(30, 80);
            entry.SliderTarget = sliderTarget;
            challenge.SliderTarget = sliderTarget;
            challenge.SliderBackground = CaptchaImageGenerator.GenerateSliderBackgroundImage(sliderTarget, 380, 44);
        }

        if (_sharedStore is not null)
            await _sharedStore.PutAsync(token, entry, cancellationToken);
        else lock (ChallengeGate(token))
        {
            _challenges[token] = entry;
            _runtimeState.Set(ChallengeDomain, token, entry, TimeSpan.FromMinutes(5));
        }
        _metrics.RecordCaptcha("generated", mode);
        return challenge;
    }

    public CaptchaVerifyResult Verify(string clientIp, string token, string answer, BehaviorData? behavior = null)
        => VerifyCoreAsync(clientIp, token, answer, behavior, complete: true, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<CaptchaVerifyResult> VerifyAsync(string clientIp, string token, string answer, BehaviorData? behavior,
        CcCaptchaTarget? target, CancellationToken cancellationToken = default)
    {
        if (!CcPolicyGeneration.TryParseKey(clientIp, out _, out _))
            return await VerifyCoreAsync(clientIp, token, answer, behavior, complete: true, cancellationToken);
        if (target is null || target.Expected.StateKey(target.PolicyKey) != clientIp)
            return new() { Message = "验证码作用域已变化", ErrorCode = "cc_captcha_scope_changed" };
        var verified = await VerifyCoreAsync(clientIp, token, answer, behavior, complete: false, cancellationToken);
        if (!verified.Success) return verified;
        try
        {
            verified.Release = await _serviceProvider.GetRequiredService<CcCaptchaReleaseClient>().ReleaseAsync(target, token, cancellationToken);
            _metrics.RecordCaptcha("verified", "success");
            return verified;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _logger.Warn("CC captcha release unconfirmed: {ErrorType}", ex.GetType().Name);
            return new() { Message = "解除尚未确认，请稍后重试", FailureStatus = 503, ErrorCode = "cc_release_unconfirmed" };
        }
    }

    private async Task<CaptchaVerifyResult> VerifyCoreAsync(string clientIp, string token, string answer, BehaviorData? behavior,
        bool complete, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
        {
            _metrics.RecordCaptcha("verified", "invalid");
            return new CaptchaVerifyResult { Success = false, Message = "参数不完整" };
        }

        CaptchaEntry? entry;
        if (_sharedStore is not null)
        {
            try { entry = await _sharedStore.TakeAsync(token, cancellationToken); }
            catch (CaptchaStoreUnavailableException)
            {
                _metrics.RecordCaptcha("verified", "unavailable");
                return new() { Message = TextLocalizer.Get("captcha.errors.store_unavailable"), FailureStatus = 503,
                    ErrorCode = "captcha_store_unavailable" };
            }
        }
        else lock (ChallengeGate(token))
        {
            if (_challenges.TryRemove(token, out var localEntry))
            {
                entry = localEntry;
                _runtimeState.Remove(ChallengeDomain, token);
            }
            else if (!_runtimeState.TryTake<CaptchaEntry>(ChallengeDomain, token, out entry))
            {
                _metrics.RecordCaptcha("verified", "invalid");
                return new CaptchaVerifyResult { Success = false, Message = "验证码已过期或无效" };
            }
        }
        if (entry is null)
        {
            _metrics.RecordCaptcha("verified", "invalid");
            return new CaptchaVerifyResult { Success = false, Message = "验证码已过期或无效" };
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (entry.Mode is not ("math" or "slider"))
            return new() { Message = "验证码已过期或无效" };

        // 检查过期
        if (_sharedStore is null && DateTime.UtcNow > entry.ExpiryTime)
        {
            _metrics.RecordCaptcha("verified", "expired");
            return new CaptchaVerifyResult { Success = false, Message = "验证码已过期" };
        }

        // 检查 IP 是否匹配
        if (entry.ClientIp != clientIp)
        {
            _metrics.RecordCaptcha("verified", "invalid");
            return new CaptchaVerifyResult { Success = false, Message = "验证失败" };
        }

        // 鼠标行为检测（两种模式都需要）
        if (behavior == null)
        {
            _metrics.RecordCaptcha("verified", "behavior");
            return new CaptchaVerifyResult { Success = false, Message = "缺少行为数据" };
        }

        if (behavior.Duration < 1000)
        {
            _logger.Warn("行为验证失败: IP={Ip}, 提交过快 ({Duration}ms)", clientIp, behavior.Duration);
            _metrics.RecordCaptcha("verified", "behavior");
            return new CaptchaVerifyResult { Success = false, Message = "验证失败，请重试" };
        }

        if (behavior.MouseTrackCount < 3)
        {
            _logger.Warn("行为验证失败: IP={Ip}, 鼠标活动过少 ({Count})", clientIp, behavior.MouseTrackCount);
            _metrics.RecordCaptcha("verified", "behavior");
            return new CaptchaVerifyResult { Success = false, Message = "验证失败，请重试" };
        }

        // 根据模式验证
        if (entry.Mode == "math")
        {
            // 算术模式：验证答案
            if (string.IsNullOrWhiteSpace(answer) || !answer.Trim().Equals(entry.Answer, StringComparison.OrdinalIgnoreCase))
            {
                _metrics.RecordCaptcha("verified", "invalid");
                return new CaptchaVerifyResult { Success = false, Message = "验证码答案错误" };
            }
        }
        else
        {
            // 滑块模式：验证滑块位置（允许 ±10% 误差）
            if (Math.Abs(behavior.SliderOffset - entry.SliderTarget) > 10)
            {
                _metrics.RecordCaptcha("verified", "invalid");
                return new CaptchaVerifyResult { Success = false, Message = "滑块验证失败" };
            }
        }

        // 验证通过，移除 pending 状态
        if (complete)
        {
            RemovePending(clientIp);
            _metrics.RecordCaptcha("verified", "success");
        }

        return new CaptchaVerifyResult { Success = true, Message = "验证通过" };
    }

    private static (string question, string answer) GenerateMathQuestion()
    {
        var ops = new[] { '+', '-', '*' };
        var op = ops[RandomNumberGenerator.GetInt32(0, ops.Length)];

        int a, b, result;

        switch (op)
        {
            case '+':
                a = RandomNumberGenerator.GetInt32(1, 50);
                b = RandomNumberGenerator.GetInt32(1, 50);
                result = a + b;
                break;
            case '-':
                a = RandomNumberGenerator.GetInt32(10, 99);
                b = RandomNumberGenerator.GetInt32(1, a); // 确保结果为正
                result = a - b;
                break;
            case '*':
                a = RandomNumberGenerator.GetInt32(2, 12);
                b = RandomNumberGenerator.GetInt32(2, 12);
                result = a * b;
                break;
            default:
                a = 1; b = 1; result = 2;
                break;
        }

        return ($"{a} {op} {b} = ?", result.ToString());
    }

    private static string GenerateToken()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static void CleanupExpired()
    {
        var expiredKeys = _challenges
            .Where(kv => DateTime.UtcNow > kv.Value.ExpiryTime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            lock (ChallengeGate(key)) _challenges.TryRemove(key, out _);
        }
    }

    /// <summary>验证码一次性 token 对应的服务端答案、客户端绑定和过期信息。</summary>
    internal sealed class CaptchaEntry
    {
        public string Mode { get; set; } = "math";
        public string Answer { get; set; } = "";
        public string ClientIp { get; set; } = "";
        public DateTime ExpiryTime { get; set; }
        public int SliderTarget { get; set; }
    }
}
