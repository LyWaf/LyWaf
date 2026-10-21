namespace LyWaf.Services.Captcha;

public sealed class CaptchaStoreOptions
{
    public string Provider { get; set; } = "Local";
    public string ClusterId { get; set; } = "";
    public string AuthorityNodeId { get; set; } = "";
    public string AuthorityEndpoint { get; set; } = "";
    public string StateDirectory { get; set; } = ".lywaf-captcha";
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;
    public int TimeoutMilliseconds { get; set; } = 2000;
    public int MaxChallenges { get; set; } = 10000;
}

public sealed class CaptchaStoreUnavailableException : Exception
{
    public CaptchaStoreUnavailableException() : base("Shared challenge store is unavailable") { }
}

internal interface ISharedCaptchaStore
{
    Task PutAsync(string token, CaptchaService.CaptchaEntry entry, CancellationToken tokenCancellation, int lifetimeMilliseconds = 300000);
    Task<CaptchaService.CaptchaEntry?> TakeAsync(string token, CancellationToken tokenCancellation);
}
