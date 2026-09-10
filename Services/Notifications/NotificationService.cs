using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Incidents;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Notifications;

public sealed record NotificationChannel(
    [property: Required, MaxLength(100)] string Id,
    [property: Required, MaxLength(100)] string Name,
    [property: Required, RegularExpression("^(webhook|smtp|plugin)$")] string Kind,
    [property: Required, MaxLength(2048)] string Destination,
    bool Enabled = true, [property: Range(1, int.MaxValue)] int MinCount = 1,
    [property: Range(1, 86400)] int MergeSeconds = 60,
    [property: MaxLength(200)] string? Site = null, [property: MaxLength(100)] string? AttackType = null,
    DateTimeOffset? SilencedUntil = null, [property: MaxLength(255)] string? SmtpHost = null,
    [property: Range(1, 65535)] int SmtpPort = 587, [property: MaxLength(254)] string? SmtpUser = null,
    [property: MaxLength(254)] string? Sender = null, [property: MaxLength(1024)] string? Password = null,
    string? ProtectedPassword = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Kind == "webhook" && (!Uri.TryCreate(Destination, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)))
            yield return new("Webhook 必须为 HTTPS 地址且不能包含凭据或片段", [nameof(Destination)]);
        if (Kind != "smtp") yield break;
        if (string.IsNullOrWhiteSpace(SmtpHost) || Uri.CheckHostName(SmtpHost) == UriHostNameType.Unknown)
            yield return new("SMTP 主机无效", [nameof(SmtpHost)]);
        if (!MailAddress.TryCreate(Sender, out _)) yield return new("发件人地址无效", [nameof(Sender)]);
        if (!MailAddress.TryCreate(Destination, out _)) yield return new("收件人地址无效", [nameof(Destination)]);
    }
}
public sealed record NotificationDelivery(string Id, string ChannelId, PluginNotification Message, string State, int Attempts,
    DateTimeOffset NextAttempt, long DeliveredCount = 0, string? Error = null);

/// <summary>按事件指纹及渠道合并通知，有界排队、静默及指数退避重试。</summary>
public sealed class NotificationService(IDataProtectionProvider protection, IHttpClientFactory clients,
    IServiceProvider services, ILogger<NotificationService> logger, string? storagePath = null) : BackgroundService
{
    private readonly object gate = new();
    private readonly IDataProtector protector = protection.CreateProtector("LyWaf.NotificationCredentials.v1");
    private readonly string path = storagePath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LyWaf.Shared.SharedData.ConfigFilePath))!, ".lywaf.notifications.json");
    private NotificationChannel[] channels = [];
    private readonly Dictionary<string, NotificationDelivery> deliveries = new();

    public NotificationChannel[] Channels() { lock (gate) return channels.Select(c => c with { Password = null, ProtectedPassword = null }).ToArray(); }
    public NotificationDelivery[] Deliveries() { lock (gate) return deliveries.Values.OrderByDescending(d => d.Message.LastSeen).ToArray(); }

    public void SaveChannel(NotificationChannel input)
    {
        Validate(input);
        lock (gate)
        {
            var existing = channels.FirstOrDefault(c => c.Id == input.Id);
            var password = string.IsNullOrEmpty(input.Password) ? existing?.ProtectedPassword : protector.Protect(input.Password);
            var candidate = channels.Where(c => c.Id != input.Id).Append(input with { Password = null, ProtectedPassword = password }).ToArray();
            if (candidate.Length > 20) throw new ArgumentException("通知渠道最多 20 个");
            Persist(candidate);
            channels = candidate;
        }
    }

    public bool DeleteChannel(string id)
    {
        lock (gate)
        {
            if (!channels.Any(c => c.Id == id)) return false;
            var next = channels.Where(c => c.Id != id).ToArray();
            Persist(next); channels = next;
            foreach (var key in deliveries.Where(d => d.Value.ChannelId == id).Select(d => d.Key).ToArray()) deliveries.Remove(key);
            return true;
        }
    }

    public void Observe(SecurityIncident incident)
    {
        if (incident.State != IncidentDisposition.Pending) return;
        lock (gate)
        {
            foreach (var channel in channels.Where(c => c.Enabled && incident.Count >= c.MinCount &&
                (string.IsNullOrEmpty(c.Site) || c.Site == incident.Site) && (string.IsNullOrEmpty(c.AttackType) || c.AttackType == incident.AttackType)))
            {
                var key = channel.Id + ":" + incident.Id;
                var message = new PluginNotification(incident.Id, incident.Site, incident.RuleId, incident.AttackType, incident.Ip, incident.Count, incident.LastSeen);
                if (deliveries.TryGetValue(key, out var previous))
                {
                    if (message.Count <= previous.Message.Count) continue;
                    deliveries[key] = previous with { Message = message,
                        State = previous.State == "sending" ? "sending" : "pending", Attempts = previous.State is "sent" or "failed" ? 0 : previous.Attempts };
                }
                else
                {
                    if (deliveries.Count >= 512)
                    {
                        var oldest = deliveries.Where(d => d.Value.State is "sent" or "failed").OrderBy(d => d.Value.Message.LastSeen).FirstOrDefault();
                        if (oldest.Key is null) continue;
                        deliveries.Remove(oldest.Key);
                    }
                    deliveries[key] = new(key, channel.Id, message, "pending", 0, DateTimeOffset.UtcNow.AddSeconds(channel.MergeSeconds));
                }
            }
        }
    }

    public bool Retry(string id)
    {
        lock (gate)
        {
            if (!deliveries.TryGetValue(id, out var delivery) || delivery.State != "failed") return false;
            deliveries[id] = delivery with { State = "pending", Attempts = 0, NextAttempt = DateTimeOffset.UtcNow };
            return true;
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("通知配置超过容量限制");
            channels = JsonSerializer.Deserialize<NotificationChannel[]>(File.ReadAllText(path)) ?? [];
            if (channels.Length > 20) throw new InvalidDataException("通知渠道超过容量限制");
            foreach (var channel in channels) Validate(channel);
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                NotificationDelivery? delivery; NotificationChannel? channel;
                lock (gate)
                {
                    delivery = deliveries.Values.FirstOrDefault(d => d.State == "pending" && d.NextAttempt <= DateTimeOffset.UtcNow &&
                        channels.Any(c => c.Id == d.ChannelId && c.Enabled && (c.SilencedUntil == null || c.SilencedUntil <= DateTimeOffset.UtcNow)));
                    channel = delivery == null ? null : channels.First(c => c.Id == delivery.ChannelId);
                    if (delivery != null) deliveries[delivery.Id] = delivery with { State = "sending" };
                }
                if (delivery == null || channel == null) continue;
                string? error = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await Send(channel, delivery.Message with { Count = delivery.Message.Count - delivery.DeliveredCount }, timeout.Token);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { error = ex.GetType().Name; logger.LogWarning("通知渠道 {Channel} 发送失败: {Error}", channel.Id, error); }
                lock (gate)
                {
                    if (!deliveries.TryGetValue(delivery.Id, out var current)) continue;
                    deliveries[delivery.Id] = error == null
                        ? current with { State = current.Message.Count > delivery.Message.Count ? "pending" : "sent", DeliveredCount = delivery.Message.Count,
                            Attempts = 0, Error = null, NextAttempt = DateTimeOffset.UtcNow.AddSeconds(channel.MergeSeconds) }
                        : current with { State = delivery.Attempts >= 2 ? "failed" : "pending", Attempts = delivery.Attempts + 1, Error = error,
                            NextAttempt = DateTimeOffset.UtcNow.AddSeconds(5 * Math.Pow(2, delivery.Attempts)) };
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task Send(NotificationChannel channel, PluginNotification message, CancellationToken ct)
    {
        switch (channel.Kind)
        {
            case "webhook":
                using (var client = clients.CreateClient("management-notifications"))
                using (var response = await client.PostAsJsonAsync(channel.Destination, message, ct)) response.EnsureSuccessStatusCode();
                break;
            case "smtp":
                using (var smtp = new SmtpClient(channel.SmtpHost!, channel.SmtpPort) { EnableSsl = true })
                using (var mail = new MailMessage(channel.Sender!, channel.Destination, $"LyWaf 告警: {message.AttackType}", JsonSerializer.Serialize(message)))
                {
                    if (!string.IsNullOrEmpty(channel.SmtpUser)) smtp.Credentials = new NetworkCredential(channel.SmtpUser,
                        channel.ProtectedPassword == null ? "" : protector.Unprotect(channel.ProtectedPassword));
                    await smtp.SendMailAsync(mail, ct);
                }
                break;
            case "plugin":
                var manager = services.GetRequiredService<IPluginManager>();
                if (manager.GetPluginState(channel.Destination) != PluginState.Running || !manager.IsPermissionGranted(channel.Destination, PluginPermission.Network) ||
                    manager.GetPlugin(channel.Destination) is not IPluginNotificationSink sink) throw new InvalidOperationException("通知插件未就绪");
                await sink.SendNotificationAsync(message, ct);
                break;
        }
    }

    internal static void Validate(NotificationChannel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Id) || channel.Id.Length > 100 || string.IsNullOrWhiteSpace(channel.Name) || channel.Name.Length > 100 ||
            channel.MergeSeconds is < 1 or > 86400 || channel.MinCount < 1 || string.IsNullOrWhiteSpace(channel.Destination) || channel.Destination.Length > 2048)
            throw new ArgumentException("通知渠道参数无效");
        if (channel.Password?.Length > 1024 || channel.SmtpUser?.Length > 254 || channel.SmtpHost?.Length > 255 || channel.Site?.Length > 200 || channel.AttackType?.Length > 100)
            throw new ArgumentException("通知渠道字段超过长度限制");
        if (channel.Kind == "webhook" && (!Uri.TryCreate(channel.Destination, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("Webhook 必须为 HTTPS 地址且不能包含用户凭据");
        if (channel.Kind == "smtp")
        {
            if (string.IsNullOrWhiteSpace(channel.SmtpHost) || channel.SmtpPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(channel.Sender)) throw new ArgumentException("SMTP 配置无效");
            try { _ = new MailAddress(channel.Sender); _ = new MailAddress(channel.Destination); }
            catch (FormatException) { throw new ArgumentException("发件人或收件人地址无效"); }
        }
        if (channel.Kind is not ("webhook" or "smtp" or "plugin")) throw new ArgumentException("通知渠道类型无效");
    }

    private void Persist(NotificationChannel[] value)
    {
        var temp = path + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
