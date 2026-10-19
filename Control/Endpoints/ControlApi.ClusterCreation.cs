using System.ComponentModel.DataAnnotations;
using System.Net;
using LyWaf.Services.Auth;
using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Control;

public static partial class ControlApi
{
    public sealed record CreateClusterRequest(
        [property: Required, RegularExpression("^[a-zA-Z0-9_-]{1,100}$")] string Id,
        [property: Required, MaxLength(100)] string Policy,
        [property: Required, MaxLength(2048)] string Address);

    private static void MapClusterCreationEndpoints(WebApplication app, int port)
    {
        app.MapPost("/api/lb/clusters", async (CreateClusterRequest request, IConfiguration config, IConfigValidator validator) =>
        {
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, true))
                return Results.BadRequest(new { success = false, message = string.Join("; ", errors.Select(e => e.ErrorMessage)) });
            if (!TryNormalizeClusterAddress(request.Address, out var uri))
                return Results.BadRequest(new { success = false, message = LyWaf.Localization.TextLocalizer.Get("api.cluster.invalidAddress") });
            var candidate = new ClusterConfig { ClusterId = request.Id, LoadBalancingPolicy = request.Policy,
                Destinations = new Dictionary<string, DestinationConfig> { ["dest1"] = new() { Address = uri.AbsoluteUri } } };
            var validation = await validator.ValidateClusterAsync(candidate);
            if (validation.Count > 0) return Results.BadRequest(new { success = false, message = validation[0].Message });
            return await ModifyLbPatch(config, patch =>
            {
                if (request.Id.Equals("cluster_unuse", StringComparison.OrdinalIgnoreCase) ||
                    config.GetSection("ReverseProxy:Clusters").GetChildren().Any(c => c.Key.Equals(request.Id, StringComparison.OrdinalIgnoreCase)) ||
                    patch.Keys.Any(k => k.Equals(request.Id, StringComparison.OrdinalIgnoreCase))) return "集群 ID 已存在或为保留名称";
                patch[request.Id] = new Dictionary<string, object>
                {
                    ["LoadBalancingPolicy"] = request.Policy,
                    ["Destinations"] = new Dictionary<string, object> { ["dest1"] = new Dictionary<string, object> { ["Address"] = uri.AbsoluteUri } }
                };
                return null;
            });
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);
    }

    internal static bool TryNormalizeClusterAddress(string address, out Uri uri)
    {
        uri = null!;
        var value = address.Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('\\')) return false;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))))
            return false;

        // Inspect the original authority: IsDefaultPort cannot distinguish explicit :443 from an omitted port.
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var portSeparator = authority.StartsWith('[') ? authority.IndexOf(']') + 1 : authority.IndexOf(':');
        var hasPort = portSeparator >= 0 && portSeparator < authority.Length && authority[portSeparator] == ':';
        if (hasPort && (!int.TryParse(authority[(portSeparator + 1)..], out var port) || port is < 1 or > 65535)) return false;
        uri = new UriBuilder(parsed) { Port = hasPort ? parsed.Port : 80 }.Uri;
        return true;
    }
}
