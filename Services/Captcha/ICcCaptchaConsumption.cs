namespace LyWaf.Services.Captcha;

/// <summary>Subject-bound consumption by a trusted verifier, not independent attestation of a human answer.</summary>
public interface ICcCaptchaConsumption
{
    Task<bool> WasConsumedAsync(string challengeDigest, string nodeId, string subject, CancellationToken token);
}
