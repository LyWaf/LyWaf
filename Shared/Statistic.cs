using System.Security.Cryptography;

namespace LyWaf.Shared;

/// <summary>
/// 统计计数与耗时类
/// 用于记录请求次数和累计耗时，支持计算平均耗时
/// </summary>
public class StaCountTime : ICloneable
{
    /// <summary>
    /// 请求次数
    /// </summary>
    public int Count { get; set; } = 0;

    /// <summary>
    /// 累计耗时（毫秒）
    /// </summary>
    public long UseTime { get; set; } = 0;

    /// <summary>
    /// 增加一次请求记录
    /// </summary>
    /// <param name="time">本次请求耗时（毫秒）</param>
    public void IncrTime(long time)
    {
        Count++;
        UseTime += time;
    }

    /// <summary>
    /// 克隆当前对象
    /// </summary>
    public object Clone()
    {
        return MemberwiseClone();
    }

    /// <summary>
    /// 平均耗时（毫秒）
    /// 计算公式: 累计耗时 / 请求次数
    /// </summary>
    public double Average
    {
        get
        {
            return UseTime / Math.Max(Count, 1);
        }
    }
}

/// <summary>
/// IP/路径统计信息类
/// 用于记录某个IP或路径的访问统计，包含总体统计和各URL的详细统计
/// </summary>
public class IpStatistic : ICloneable
{
    /// <summary>
    /// 各URL路径的访问统计
    /// Key: 请求路径（如 /api/users）
    /// Value: 该路径的访问次数和耗时统计
    /// 示例: { "/api/config": { Count: 10, UseTime: 1000 } }
    /// </summary>
    public Dictionary<string, StaCountTime> UrlCostTime = [];

    /// <summary>
    /// 总体访问统计（所有URL的汇总）
    /// 包含总请求次数和总耗时
    /// </summary>
    public StaCountTime CountTime { get; set; } = new();

    /// <summary>
    /// 克隆当前对象（深拷贝）
    /// </summary>
    public object Clone()
    {
        return new IpStatistic
        {
            CountTime = (StaCountTime)CountTime.Clone(),
            UrlCostTime = new Dictionary<string, StaCountTime>(UrlCostTime),
        };
    }
}

/// <summary>
/// 客户端统计信息类
/// 用于记录单个客户端的访问行为统计
/// </summary>
public class ClientStatistic
{
    /// <summary>
    /// 各URL路径的访问次数
    /// Key: 请求路径（如 /api/users）
    /// Value: 访问次数
    /// 示例: { "/api/config": 10, "/api/users": 5 }
    /// </summary>
    public Dictionary<string, int> UrlVisitTimes = [];

    /// <summary>
    /// 总体访问统计
    /// 包含总请求次数和总耗时
    /// </summary>
    public StaCountTime CountTime { get; set; } = new();
}

/// <summary>
/// 请求简要信息类
/// 用于记录单次请求的关键信息，用于详细访问记录
/// </summary>
/// <param name="context">HTTP上下文，用于获取响应状态码</param>
/// <param name="path">请求路径</param>
/// <param name="costTime">请求耗时（毫秒）</param>
public class ReqestShortMsg(HttpContext context, string path, long costTime)
{
    /// <summary>
    /// 请求路径（如 /api/users）
    /// </summary>
    public string Path { get; set; } = path;

    /// <summary>
    /// 请求耗时（毫秒）
    /// </summary>
    public long CostTime { get; set; } = costTime;

    /// <summary>
    /// 请求时间（UTC）
    /// </summary>
    public DateTime ReqTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// HTTP响应状态码（如 200, 404, 500）
    /// </summary>
    public int StatusCode { get; set; } = context.Response.StatusCode;
}
