using System.ComponentModel.DataAnnotations;

namespace LyWaf.Services.ManagementApi;

/// <summary>验证使用字符串传输的枚举名称，与管理接口忽略大小写的解析保持一致。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EnumNameAttribute(Type enumType) : ValidationAttribute
{
    public Type EnumType { get; } = enumType.IsEnum ? enumType : throw new ArgumentException("Expected an enum", nameof(enumType));

    public override bool IsValid(object? value) => value == null || value is string text &&
        Enum.GetNames(EnumType).Any(name => name.Equals(text, StringComparison.OrdinalIgnoreCase));

    public override string FormatErrorMessage(string name) => $"{name}: allowed values: {string.Join(", ", Enum.GetNames(EnumType))}";
}
