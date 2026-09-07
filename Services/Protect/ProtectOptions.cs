
using System.Threading.RateLimiting;

namespace LyWaf.Services.Protect;

public class ProtectOptions
{
    public bool OpenArgsCheck { get; set; } = true;
    public bool OpenPostCheck { get; set; } = false;
    public string CheckArgsFile { get; set; } = "Rules/args.check";
    public string CheckPostFile { get; set; } = "Rules/post.check";
    public List<string> RegexArgsList { get; set; } = [];
    public List<string> RegexPostList { get; set; } = [];
    // 最高的请求body大小
    public int? MaxRequestBodySize { get; set; } = null;
    // WAF 内容检查的独立读取上限；即使代理允许更大请求，也不会把整个 Body 读入内存。
    public int InspectionMaxBodyBytes { get; set; } = 1024 * 1024;
    public int InspectionMaxValueLength { get; set; } = 64 * 1024;
    public int InspectionMaxCandidates { get; set; } = 128;
    public int InspectionDecodeDepth { get; set; } = 3;
    public bool RejectProtocolAnomalies { get; set; } = true;
    public bool SemanticDetectionEnabled { get; set; } = true;
    public string SemanticDetectionAction { get; set; } = "Observe";
}
