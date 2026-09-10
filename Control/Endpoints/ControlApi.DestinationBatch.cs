namespace LyWaf.Control;

public static partial class ControlApi
{
    internal sealed record DestinationBatchItem(string Id, bool Success, string? Code = null, string? Message = null);

    internal static List<DestinationBatchItem> ApplyDestinationRemovals(IEnumerable<string> ids,
        Dictionary<string, object> destinations, Func<string, bool> isPatchDestination)
    {
        var results = new List<DestinationBatchItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Contains(':'))
                results.Add(new(id ?? "", false, "invalid_id", "目标 ID 无效"));
            else if (!isPatchDestination(id))
                results.Add(new(id, false, "not_patch_destination", "目标不存在或属于默认配置，不能删除"));
            else if (!destinations.Remove(id)) results.Add(new(id, false, "not_found", "补丁中不存在该目标"));
            else results.Add(new(id, true));
        }
        return results;
    }

    private static async Task<IResult> RemoveDestinationsBatchAsync(BatchRemoveDestinationsRequest request, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(request.ClusterId) || request.ClusterId.Length > 200 || request.ClusterId.Contains(':'))
            throw new ArgumentException("集群 ID 无效", nameof(request.ClusterId));
        if (request.DestinationIds is not { Count: > 0 and <= 100 })
            throw new ArgumentException("每批须包含 1 至 100 个目标", nameof(request.DestinationIds));
        var ids = request.DestinationIds.Distinct(StringComparer.Ordinal).ToArray();
        var results = new List<DestinationBatchItem>();
        try
        {
            await ModifyLbPatch(config, patch =>
            {
                var cluster = EnsurePatchCluster(patch, request.ClusterId);
                var destinations = EnsurePatchDestinations(cluster);
                results = ApplyDestinationRemovals(ids, destinations, id =>
                    DetectConfigSource(config, $"ReverseProxy:Clusters:{request.ClusterId}:Destinations:{id}") == ConfigSource.PatchConfig);
                return results.Any(item => item.Success) ? null : "没有可删除的目标";
            });
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Error(ex, "批量删除目标发布失败");
            results = ids.Select(id => results.FirstOrDefault(item => item.Id == id && !item.Success)
                ?? new DestinationBatchItem(id ?? "", false, "publish_failed", "配置发布失败，未确认删除生效")).ToList();
        }
        return Results.Ok(new { success = true, allSucceeded = results.All(item => item.Success),
            results, succeeded = results.Count(item => item.Success), failed = results.Count(item => !item.Success) });
    }
}
