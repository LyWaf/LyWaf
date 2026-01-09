
using System.Collections.Concurrent;
using LyWaf.Services.SpeedLimit;
using LyWaf.Struct;

namespace LyWaf.Shared;
public static class SharedData
{

    public static readonly ExpiringSafeDictionary<string, object> IpDict = new();

    public static readonly ExpiringSafeDictionary<string, object> Limit = new();

    public static readonly ExpiringSafeDictionary<string, object> CacheDict = new();

    public static readonly ExpiringSafeDictionary<string, IpStatistic> DestStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));
    public static readonly ExpiringSafeDictionary<string, IpStatistic> ReqStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));
    public static readonly ExpiringSafeDictionary<string, IpStatistic> ClientStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));
    public static readonly ExpiringSafeDictionary<string, long> ClientTimes =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));
    public static readonly ExpiringSafeDictionary<string, long> NewClientVisits =
                            new(defaultExpiration: TimeSpan.FromMinutes(60),
                                cleanupInterval: TimeSpan.FromMinutes(30));

    public static readonly ExpiringSafeDictionary<string, long> LimitCcStas =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(30));
    public static readonly ExpiringSafeDictionary<string, LinkedList<ReqestShortMsg>> ClientDetailVisits =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(10));
    public static readonly ExpiringSafeDictionary<string, string> ClientFb =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(10));

    public static readonly ExpiringSafeDictionary<string, ClientThrottledLimit> ClientThrottled =
                            new(defaultExpiration: TimeSpan.FromMinutes(10),
                                cleanupInterval: TimeSpan.FromMinutes(20));
}