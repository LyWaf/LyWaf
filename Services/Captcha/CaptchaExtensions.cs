using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Captcha;

public static class CaptchaExtensions
{
    public static IServiceCollection AddLyWafCaptcha(this IServiceCollection services, IConfiguration configuration)
    {
        var communication = configuration.GetValue<bool>("NodeCommunication:Enabled");
        var distributed = communication || configuration.GetValue<bool>("RuntimeState:Enabled");
        services.AddOptions<CaptchaStoreOptions>().Bind(configuration.GetSection("CaptchaStore"))
            .Validate(o => o.Provider is "Local" or "Authority" && (!distributed || o.Provider == "Authority")
                && (o.Provider != "Authority" || communication && NodeMessageAuthentication.ValidId(o.ClusterId)
                    && o.ClusterId == configuration["NodeCommunication:ClusterId"]
                    && NodeMessageAuthentication.ValidId(o.AuthorityNodeId) && !string.IsNullOrWhiteSpace(o.StateDirectory)
                    && o.MaxBytes is >= 16384 and <= 512L * 1024 * 1024
                    && o.TimeoutMilliseconds is >= 100 and <= 10000 && o.MaxChallenges is >= 1 and <= 100000),
                "Multi-node challenges require the built-in Authority provider, node identity and valid bounds")
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CaptchaStoreOptions>>(sp => new ChallengeSecretValidation(configuration,
            sp.GetService<NodeSetupStore>()));
        if (configuration.GetValue<string>("CaptchaStore:Provider") == "Authority")
        {
            services.AddSingleton<FileCaptchaAuthority>();
            services.AddSingleton<AuthorityCaptchaStore>();
            services.AddSingleton<ISharedCaptchaStore>(sp => sp.GetRequiredService<AuthorityCaptchaStore>());
            services.AddSingleton<ICcCaptchaConsumption>(sp => sp.GetRequiredService<AuthorityCaptchaStore>());
            services.AddHostedService(sp => sp.GetRequiredService<AuthorityCaptchaStore>());
        }
        services.AddSingleton<ICaptchaService, CaptchaService>();
        return services;
    }

    private sealed class ChallengeSecretValidation(IConfiguration configuration, NodeSetupStore? setup) : IValidateOptions<CaptchaStoreOptions>
    {
        public ValidateOptionsResult Validate(string? name, CaptchaStoreOptions options)
        {
            var secret = setup?.ChallengeSecret;
            if (string.IsNullOrEmpty(secret)) secret = configuration["AntiBot:Secret"] ?? "";
            return options.Provider != "Authority" || !configuration.GetValue<bool>("AntiBot:Enabled")
                || !string.Equals(configuration["AntiBot:Mode"], "Challenge", StringComparison.OrdinalIgnoreCase)
                || System.Text.Encoding.UTF8.GetByteCount(secret) >= 32
                ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Multi-node challenge mode requires a shared signing secret");
        }
    }
}
