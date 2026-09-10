using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;

namespace LyWaf.Control;

/// <summary>Validates an IP literal, optionally including an IPv4 or IPv6 CIDR prefix.</summary>
public sealed class IpAddressAttribute(bool allowCidr = false) : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not string text || text.Length > 128) return false;
        var parts = text.Trim().Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address)) return false;
        return parts.Length == 1 || allowCidr && int.TryParse(parts[1], out var prefix) && prefix >= 0
            && prefix <= (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }
}
