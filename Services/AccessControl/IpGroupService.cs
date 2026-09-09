using System.Net;
using System.Text.Json;

namespace LyWaf.Services.AccessControl;

public sealed class IpGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Entries { get; set; } = [];
    public bool WhitelistEnabled { get; set; }
    public bool BlacklistEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface IIpGroupService
{
    IReadOnlyList<IpGroup> GetAll();
    IpGroup Create(string name, string? description, IEnumerable<string> entries);
    IpGroup? Update(string id, string name, string? description, IEnumerable<string> entries);
    bool Delete(string id);
    IpGroup? SetUsage(string id, bool whitelistEnabled, bool blacklistEnabled);
    event Action? Changed;
}

public sealed class IpGroupService : IIpGroupService
{
    public event Action? Changed;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _lock = new();
    private readonly string _filePath;
    private List<IpGroup> _groups;

    public IpGroupService(string? filePath = null)
    {
        var dataDirectory = filePath == null
            ? Path.Combine(AppContext.BaseDirectory, "datas")
            : Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(dataDirectory);
        _filePath = filePath ?? Path.Combine(dataDirectory, "ip-groups.json");
        _groups = Load();
    }

    public IReadOnlyList<IpGroup> GetAll()
    {
        lock (_lock)
        {
            return _groups
                .OrderByDescending(group => group.UpdatedAt)
                .Select(Clone)
                .ToList();
        }
    }

    public IpGroup Create(string name, string? description, IEnumerable<string> entries)
    {
        lock (_lock)
        {
            name = ValidateName(name, null);
            var now = DateTime.UtcNow;
            var group = new IpGroup
            {
                Name = name,
                Description = (description ?? "").Trim(),
                Entries = NormalizeEntries(entries),
                CreatedAt = now,
                UpdatedAt = now
            };
            _groups.Add(group);
            Save();
            Changed?.Invoke();
            return Clone(group);
        }
    }

    public IpGroup? Update(string id, string name, string? description, IEnumerable<string> entries)
    {
        lock (_lock)
        {
            var group = _groups.FirstOrDefault(item => item.Id == id);
            if (group == null) return null;

            group.Name = ValidateName(name, id);
            group.Description = (description ?? "").Trim();
            group.Entries = NormalizeEntries(entries);
            group.UpdatedAt = DateTime.UtcNow;
            Save();
            Changed?.Invoke();
            return Clone(group);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _groups.RemoveAll(group => group.Id == id) > 0;
            if (removed)
            {
                Save();
                Changed?.Invoke();
            }
            return removed;
        }
    }

    public IpGroup? SetUsage(string id, bool whitelistEnabled, bool blacklistEnabled)
    {
        lock (_lock)
        {
            var group = _groups.FirstOrDefault(item => item.Id == id);
            if (group == null) return null;
            if (whitelistEnabled && blacklistEnabled)
                throw new ArgumentException("同一个 IP 组不能同时加入白名单和黑名单");
            group.WhitelistEnabled = whitelistEnabled;
            group.BlacklistEnabled = blacklistEnabled;
            group.UpdatedAt = DateTime.UtcNow;
            Save();
            Changed?.Invoke();
            return Clone(group);
        }
    }

    private string ValidateName(string name, string? currentId)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("IP 组名称不能为空");
        if (name.Length > 64) throw new ArgumentException("IP 组名称不能超过 64 个字符");
        if (_groups.Any(group => group.Id != currentId && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("IP 组名称已存在");
        return name;
    }

    private static List<string> NormalizeEntries(IEnumerable<string> entries)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries ?? [])
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (!IsValidIpOrCidr(value)) throw new ArgumentException($"无效的 IP 或 CIDR: {value}");
            if (seen.Add(value)) result.Add(value);
        }
        return result;
    }

    private static bool IsValidIpOrCidr(string value)
    {
        var parts = value.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address)) return false;
        if (parts.Length == 1) return true;
        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= maxPrefix;
    }

    private List<IpGroup> Load()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<IpGroup>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_groups, JsonOptions));
        File.Move(tempPath, _filePath, true);
    }

    private static IpGroup Clone(IpGroup source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Entries = [.. source.Entries],
        WhitelistEnabled = source.WhitelistEnabled,
        BlacklistEnabled = source.BlacklistEnabled,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };
}
