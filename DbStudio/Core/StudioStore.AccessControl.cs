using System.Security.Claims;

namespace DbStudio.Core;

/// <summary>
/// 用户与角色管理。保护管理员入口并确保权限修改对后续操作立即生效。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 仅向权限管理员返回角色及权限组合。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    public List<Role> Roles(ClaimsPrincipal principal)
    {
        Require(principal, Permission.Security);
        using var db = Open();
        using var cmd = Command(db, "SELECT Id,Name,Permissions FROM Roles");
        using var rows = cmd.ExecuteReader();
        var result = new List<Role>();
        while (rows.Read())
        {
            result.Add(new(rows.GetString(0), rows.GetString(1), (Permission)rows.GetInt32(2)));
        }

        return result;
    }

    /// <summary>
    /// 仅向权限管理员返回成员资料，不暴露密码哈希或安全戳。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    public List<UserInfo> Users(ClaimsPrincipal principal)
    {
        Require(principal, Permission.Security);
        using var db = Open();
        using var cmd = Command(db, "SELECT Id,Login,DisplayName,RoleId,Enabled FROM Users");
        using var rows = cmd.ExecuteReader();
        var result = new List<UserInfo>();
        while (rows.Read())
        {
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetBoolean(4)));
        }

        return result;
    }

    /// <summary>
    /// 新增或更新角色；管理员角色不可修改，所有角色保留查看权限。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="role">待保存的角色名称和权限。</param>
    public void SaveRole(ClaimsPrincipal principal, Role role)
    {
        lock (gate)
        {
            var actor = Require(principal, Permission.Security);
            if (role.Id == "admin")
            {
                throw new InvalidOperationException("系统管理员角色受保护，不能修改。");
            }

            if (string.IsNullOrWhiteSpace(role.Name) || role.Name.Length > 50 || (role.Permissions & ~Permission.All) != 0)
            {
                throw new InvalidOperationException("角色名称或权限无效。");
            }

            role = role with
            {
                Permissions = role.Permissions | Permission.Read
            };
            using var db = Open();
            using var tx = db.BeginTransaction();
            Execute(
                db,
                "INSERT INTO Roles VALUES($id,$name,$permissions) ON CONFLICT(Id) DO UPDATE SET Name=$name,Permissions=$permissions",
                ("$id", role.Id),
                ("$name", role.Name.Trim()),
                ("$permissions", (int)role.Permissions));
            Log(db, actor.DisplayName, "更新角色权限", role.Name);
            tx.Commit();
        }
    }

    /// <summary>
    /// 统一验证新密码长度与字符组成，供创建用户和修改密码复用。
    /// </summary>
    private static void ValidatePassword(string password)
    {
        if (password.Length < 10 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            throw new InvalidOperationException("密码至少 10 位，且包含字母和数字。");
        }
    }

    /// <summary>
    /// 保存成员资料，保护当前账号与最后一位管理员；更新安全戳使旧会话失效。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="user">待保存的账号资料。</param>
    /// <param name="password">待验证或设置的明文密码，仅用于哈希验证或生成。</param>
    public void SaveUser(ClaimsPrincipal principal, UserInfo user, string password)
    {
        lock (gate)
        {
            var actor = Require(principal, Permission.Security);
            if (!System.Text.RegularExpressions.Regex.IsMatch(user.Login, "^[a-zA-Z0-9_.-]{3,50}$") || string.IsNullOrWhiteSpace(user.DisplayName) || user.DisplayName.Length > 80)
            {
                throw new InvalidOperationException("账号需 3–50 位字母、数字或 ._-，显示名必填且最多 80 字符。");
            }

            using var db = Open();
            using var tx = db.BeginTransaction();
            var exists = Scalar(db, "SELECT count(*) FROM Users WHERE Id=$id", ("$id", user.Id)) != "0";
            if (!exists || password != "")
            {
                ValidatePassword(password);
            }

            if (user.Id == actor.Id && (!user.Enabled || user.RoleId != actor.RoleId))
            {
                throw new InvalidOperationException("不能禁用自己或修改自己的角色。");
            }

            if (Scalar(db, "SELECT count(*) FROM Users WHERE Id=$id AND RoleId='admin' AND Enabled=1", ("$id", user.Id)) == "1" && (!user.Enabled || user.RoleId != "admin") && Scalar(db, "SELECT count(*) FROM Users WHERE RoleId='admin' AND Enabled=1") == "1")
            {
                throw new InvalidOperationException("必须保留至少一位启用的管理员。");
            }

            if (Scalar(db, "SELECT count(*) FROM Roles WHERE Id=$id", ("$id", user.RoleId)) == "0")
            {
                throw new InvalidOperationException("角色不存在。");
            }

            if (exists)
            {
                Execute(
                    db,
                    "UPDATE Users SET Login=$login,DisplayName=$name,RoleId=$role,Enabled=$enabled,Stamp=$stamp WHERE Id=$id",
                    ("$login", user.Login),
                    ("$name", user.DisplayName),
                    ("$role", user.RoleId),
                    ("$enabled", user.Enabled),
                    ("$stamp", Guid.NewGuid().ToString()),
                    ("$id", user.Id));
                if (password != "")
                {
                    Execute(
                        db,
                        "UPDATE Users SET PasswordHash=$hash,Failures=0,LockUntil='' WHERE Id=$id",
                        ("$hash", hasher.HashPassword(user.Id, password)),
                        ("$id", user.Id));
                }
            }
            else
            {
                Execute(
                    db,
                    "INSERT INTO Users(Id,Login,DisplayName,RoleId,Enabled,PasswordHash,Stamp) VALUES($id,$login,$name,$role,$enabled,$hash,$stamp)",
                    ("$id", user.Id),
                    ("$login", user.Login),
                    ("$name", user.DisplayName),
                    ("$role", user.RoleId),
                    ("$enabled", user.Enabled),
                    ("$hash", hasher.HashPassword(user.Id, password)),
                    ("$stamp", Guid.NewGuid().ToString()));
            }

            Log(db, actor.DisplayName, exists ? "更新用户" : "新增用户", user.Login);
            tx.Commit();
        }
    }
}
