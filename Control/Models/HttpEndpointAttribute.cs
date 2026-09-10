using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

/// <summary>上游地址只接受 HTTP(S) 绝对地址，不允许嵌入凭据或片段。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpEndpointAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value == null || value is string text && text.Length <= 8192 &&
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}
