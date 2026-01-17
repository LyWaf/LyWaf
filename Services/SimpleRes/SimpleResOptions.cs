using System.Text;

namespace LyWaf.Services.SimpleRes;

/// <summary>
/// 简单响应配置选项
/// </summary>
public class SimpleResOptions
{
    /// <summary>
    /// 简单响应映射表
    /// Key 格式: simpleres_xxx (全局唯一)
    /// Value: 响应内容配置
    /// </summary>
    public Dictionary<string, SimpleResItem> Items { get; set; } = [];
}

/// <summary>
/// 单个简单响应配置项
/// </summary>
public class SimpleResItem
{
    /// <summary>
    /// 响应体内容
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// HTTP 状态码，默认 200
    /// </summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// Content-Type，默认 text/plain
    /// </summary>
    public string ContentType { get; set; } = "text/plain";

    /// <summary>
    /// 响应编码，默认 UTF-8
    /// </summary>
    public string Charset { get; set; } = "utf-8";

    /// <summary>
    /// 额外的响应头
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// 是否显示请求头信息（调试用）
    /// 如果为 true，会在响应体末尾追加所有请求头信息
    /// </summary>
    public bool ShowReq { get; set; } = false;

    /// <summary>
    /// 获取编码对象
    /// </summary>
    public Encoding GetEncoding()
    {
        return Charset?.ToLower() switch
        {
            "utf-8" or "utf8" => Encoding.UTF8,
            "ascii" => Encoding.ASCII,
            "unicode" or "utf-16" or "utf16" => Encoding.Unicode,
            "utf-32" or "utf32" => Encoding.UTF32,
            "gb2312" or "gbk" => Encoding.GetEncoding("gb2312"),
            "iso-8859-1" or "latin1" => Encoding.Latin1,
            _ => Encoding.UTF8
        };
    }

    /// <summary>
    /// 获取完整的 Content-Type（包含 charset）
    /// </summary>
    public string GetFullContentType()
    {
        if (ContentType.Contains("charset", StringComparison.OrdinalIgnoreCase))
        {
            return ContentType;
        }
        return $"{ContentType}; charset={Charset}";
    }
}
