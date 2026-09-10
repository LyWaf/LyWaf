namespace LyWaf.Services.Auth;

/// <summary>持久化业务角色；内置角色不允许覆盖。</summary>
public sealed record ManagementRoleDefinition(string Name, string[] Permissions, bool BuiltIn = false);

public partial class AuthService
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<ManagementRoleDefinition> GetRoles()
    {
        ReloadConfigIfStale();
        return [new("admin", ManagementPermissions.ForRole("admin").ToArray(), true),
            new("auditor", [ManagementPermissions.Read], true),
            .. _config.Roles.Select(role => new ManagementRoleDefinition(role.Name, [.. role.Permissions]))];
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void SaveRole(string name, string[] permissions)
    {
        ReloadConfigIfStale();
        name = name.Trim().ToLowerInvariant();
        if (name.Length is < 3 or > 48 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            name is ManagementRoles.Admin or ManagementRoles.Auditor)
            throw new ArgumentException("角色名称必须是 3-48 位字母、数字或短横线，且不能覆盖内置角色", nameof(name));
        if (permissions is null || permissions.Length == 0 || permissions.Any(p =>
                !ManagementPermissions.ForRole("admin").Contains(p)))
            throw new ArgumentException("权限列表无效", nameof(permissions));
        var definition = new ManagementRoleDefinition(name, permissions.Distinct().Order().ToArray());
        var index = _config.Roles.FindIndex(role => role.Name == name);
        if (index < 0) _config.Roles.Add(definition);
        else _config.Roles[index] = definition;
        foreach (var user in _config.Users.Where(user => user.Role == name)) user.SessionVersion++;
        SaveConfig();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool DeleteRole(string name)
    {
        ReloadConfigIfStale();
        if (_config.Users.Any(user => user.Role == name)) return false;
        if (_config.Roles.RemoveAll(role => role.Name == name) == 0) return false;
        SaveConfig();
        return true;
    }

    private bool RoleExists(string name) => name is ManagementRoles.Admin or ManagementRoles.Auditor ||
        _config.Roles.Any(role => role.Name == name);

    private IReadOnlySet<string> ResolvePermissions(string name) =>
        name is ManagementRoles.Admin or ManagementRoles.Auditor ? ManagementPermissions.ForRole(name) :
        new HashSet<string>(_config.Roles.FirstOrDefault(role => role.Name == name)?.Permissions ?? [], StringComparer.Ordinal);
}
