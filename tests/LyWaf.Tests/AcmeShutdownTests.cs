using System.Collections.Concurrent;
using System.Reflection;
using LyWaf.Services.Acme;
using Xunit;

namespace LyWaf.Tests;

public sealed class AcmeShutdownTests
{
    [Fact]
    public async Task StopCancelsAndJoinsPendingRenewalBeforeResourceDisposal()
    {
        using var service = Create();
        var certificates = (ConcurrentDictionary<string, DomainCertificate>)typeof(AcmeService)
            .GetField("_certificateInfos", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        certificates["api.example.com"] = new() { Domain = "api.example.com", ExpiresAt = DateTime.UtcNow.AddDays(-1) };
        var gate = Gate(service);
        await gate.WaitAsync();
        try
        {
            var renewal = service.ScheduleRenewal();
            Assert.False(renewal.IsCompleted);
            Assert.Same(renewal, service.ScheduleRenewal());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
            Assert.True(renewal.IsCompletedSuccessfully);
            Assert.True(service.ScheduleRenewal().IsCompletedSuccessfully);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task StopJoinsSharedOnDemandIssuanceAndRejectsNewWork()
    {
        using var service = Create();
        var gate = Gate(service);
        await gate.WaitAsync();
        try
        {
            var first = service.GetOrRequestCertificateAsync("api.example.com");
            var second = service.GetOrRequestCertificateAsync("api.example.com");
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
            Assert.Null(await first);
            Assert.Null(await second);
            Assert.Null(await service.GetOrRequestCertificateAsync("api.example.com"));
        }
        finally { gate.Release(); }
    }

    private static AcmeService Create() => new(new StaticOptionsMonitor<AcmeOptions>(new()
    {
        InternalCaEnabled = true, OnDemandEnabled = true, OnDemandAllowedDomains = ["api.example.com"],
    }));

    private static SemaphoreSlim Gate(AcmeService service) => (SemaphoreSlim)typeof(AcmeService)
        .GetField("_semaphore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
