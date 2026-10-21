using System.Text.Json;
using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.Captcha;
using LyWaf.Localization;

namespace LyWaf.Services.AntiBot;

public sealed class AntiBotMiddleware(
    RequestDelegate next,
    IOptionsMonitor<AntiBotOptions> options,
    IAntiBotAnalyzer analyzer,
    IAntiBotChallengeService challenges)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task InvokeAsync(HttpContext context)
    {
        var current = options.CurrentValue;
        if (!current.Enabled) { await next(context); return; }
        if (context.Request.Path.Equals(current.ChallengePath, StringComparison.OrdinalIgnoreCase))
        {
            await VerifyAsync(context, current);
            return;
        }

        var assessment = analyzer.Assess(context);
        context.Items["AntiBot.Assessment"] = assessment;
        if (assessment.Bypassed || challenges.ValidateSession(context,
                context.Request.Cookies[current.CookieName]))
        { await next(context); return; }

        if (assessment.RiskScore >= Math.Clamp(current.ChallengeScore, 1, 100))
            Logger.Warn("Anti-Bot 风险: Class={Class}, Score={Score}, Fingerprint={Fingerprint}, Reasons={Reasons}, Path={Path}",
                assessment.ClientClass, assessment.RiskScore, assessment.Fingerprint,
                string.Join(',', assessment.Reasons), context.Request.Path);

        if (assessment.RiskScore >= Math.Clamp(current.BlockScore, 1, 100) &&
            current.Mode.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "antibot_blocked", assessment.Fingerprint }, context.RequestAborted);
            return;
        }
        if (assessment.RiskScore >= Math.Clamp(current.ChallengeScore, 1, 100) &&
            current.Mode.Equals("Challenge", StringComparison.OrdinalIgnoreCase))
        {
            await WriteChallengeAsync(context, current);
            return;
        }
        await next(context);
    }

    private async Task VerifyAsync(HttpContext context, AntiBotOptions current)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.ContentLength is null or > 16_384)
        { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        ChallengeAnswer? answer;
        try { answer = await context.Request.ReadFromJsonAsync<ChallengeAnswer>(cancellationToken: context.RequestAborted); }
        catch (JsonException) { answer = null; }
        (bool Success, string ReturnPath) result;
        try { result = answer is null ? (false, "/") : await challenges.VerifyProofAsync(context, answer.Token, answer.Counter); }
        catch (CaptchaStoreUnavailableException) { await Unavailable(context); return; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        if (!result.Success)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        context.Response.Cookies.Append(current.CookieName, challenges.CreateSession(context), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(Math.Clamp(current.SessionMinutes, 1, 1440)), Path = "/"
        });
        await context.Response.WriteAsJsonAsync(new { success = true, redirect = result.ReturnPath }, context.RequestAborted);
    }

    private async Task WriteChallengeAsync(HttpContext context, AntiBotOptions current)
    {
        string token;
        try { token = await challenges.CreateChallengeAsync(context); }
        catch (CaptchaStoreUnavailableException) { await Unavailable(context); return; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        var tokenJson = JsonSerializer.Serialize(token);
        var pathJson = JsonSerializer.Serialize(current.ChallengePath);
        var difficulty = Math.Clamp(current.ProofLeadingZeroNibbles, 1, 6);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync($$"""
<!doctype html><meta charset="utf-8"><meta name="robots" content="noindex,nofollow">
<title>浏览器验证</title><main><h1>正在验证浏览器</h1><p id="state">请稍候...</p></main>
<script>
(async()=>{const token={{tokenJson}},target={{pathJson}},need={{difficulty}};let n=0;
for(;;n++){const b=new TextEncoder().encode(token+":"+n),h=new Uint8Array(await crypto.subtle.digest("SHA-256",b));
let s="";for(const x of h)s+=x.toString(16).padStart(2,"0");if(s.startsWith("0".repeat(need)))break;}
const r=await fetch(target,{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({token,counter:n})});
if(!r.ok)throw 0;location.replace((await r.json()).redirect);})().catch(()=>document.querySelector("#state").textContent="验证失败，请刷新重试");
</script>
""", context.RequestAborted);
    }

    private sealed record ChallengeAnswer(string Token, long Counter);
    private static async Task Unavailable(HttpContext context)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { errorCode = "captcha_store_unavailable",
            messageKey = "captcha.errors.store_unavailable", message = TextLocalizer.Get("captcha.errors.store_unavailable") }, context.RequestAborted);
    }
}
