
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
}
