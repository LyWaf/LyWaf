using System.Net;
using System.Text.Json;
using LyWaf.Services.Incidents;
using LyWaf.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class NotificationTests
{
    private sealed class Handler : HttpMessageHandler, IHttpClientFactory
    {
        public readonly TaskCompletionSource<string> Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.TrySetResult(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task WebhookMergesCountsAndSmtpPasswordIsNotExposed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-notification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var handler = new Handler();
            using var services = new ServiceCollection().BuildServiceProvider();
            var path = Path.Combine(directory, "channels.json");
            using var notifications = new NotificationService(new EphemeralDataProtectionProvider(), handler, services,
                NullLogger<NotificationService>.Instance, path);
            notifications.SaveChannel(new("mail", "mail", "smtp", "owner@example.test", false, SmtpHost: "smtp.example.test", Sender: "waf@example.test", Password: "private-password"));
            Assert.DoesNotContain("private-password", File.ReadAllText(path));
            Assert.Null(Assert.Single(notifications.Channels()).ProtectedPassword);
            notifications.SaveChannel(new("hook", "hook", "webhook", "https://example.test/hook", MergeSeconds: 1));
            await notifications.StartAsync(default);
            var now = DateTimeOffset.UtcNow;
            var incident = new SecurityIncident("incident", "site", "host", "127.0.0.1", "rule", "WAF", 1, now, now,
                IncidentDisposition.Pending, 0, [], []);
            notifications.Observe(incident);
            notifications.Observe(incident with { Count = 5 });
            var body = await handler.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var document = JsonDocument.Parse(body);
            Assert.Equal(5, document.RootElement.GetProperty("count").GetInt64());
            await notifications.StopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PlaintextWebhookAndInvalidChannelAreRejected()
    {
        Assert.Throws<ArgumentException>(() => NotificationService.Validate(new("id", "name", "webhook", "http://example.test")));
        Assert.Throws<ArgumentException>(() => NotificationService.Validate(new("id", "name", "unknown", "destination")));
    }
}
