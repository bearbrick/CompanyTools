using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace DbStudio.Core;

/// <summary>
/// 会话验证、登录锁定与密码更新。权限和安全戳每次从数据库读取。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 校验账号启用状态及安全戳，返回最新角色权限；失效会话返回 null。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    public SessionUser? Current(ClaimsPrincipal principal)
    {
        using var db = Open();
        using var cmd = Command(
            db,
            "SELECT u.Id,u.Login,u.DisplayName,u.RoleId,r.Name,r.Permissions,u.Stamp FROM Users u JOIN Roles r ON r.Id=u.RoleId WHERE u.Id=$id AND u.Enabled=1",
            ("$id", principal.UserId()));
        using var row = cmd.ExecuteReader();
        if (!row.Read() || principal.FindFirstValue("stamp") != row.GetString(6))
        {
            return null;
        }

        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), (Permission)row.GetInt32(5));
    }

    /// <summary>
    /// 强制要求会话有效且拥有全部指定权限，否则抛出授权异常。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="permission">操作所需权限组合，必须全部满足。</param>
    public SessionUser Require(ClaimsPrincipal principal, Permission permission)
    {
        var user = Current(principal);
        if (user == null || (user.Permissions & permission) != permission)
        {
            throw new UnauthorizedAccessException("当前账号没有此操作权限，或登录已失效。请重新登录／联系管理员。");
        }

        return user;
    }

    /// <summary>
    /// 验证密码并维护失败次数；连续五次失败锁定十分钟，成功后签发安全戳声明。
    /// </summary>
    /// <param name="login">登录账号。</param>
    /// <param name="password">待验证或设置的明文密码，仅用于哈希验证或生成。</param>
    public ClaimsPrincipal? Login(string login, string password)
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(
                db,
                "SELECT Id,DisplayName,PasswordHash,Stamp,Failures,LockUntil FROM Users WHERE Login=$login AND Enabled=1",
                ("$login", login));
            using var row = cmd.ExecuteReader();
            if (!row.Read())
            {
                hasher.VerifyHashedPassword("dummy", hasher.HashPassword("dummy", "not-the-password"), password);
                return null;
            }
            var id = row.GetString(0);
            var name = row.GetString(1);
            var hash = row.GetString(2);
            var stamp = row.GetString(3);
            var failures = row.GetInt32(4);
            var until = row.GetString(5);
            row.Close();
            if (DateTimeOffset.TryParse(until, out var locked) && locked > DateTimeOffset.UtcNow)
            {
                return null;
            }

            if (hasher.VerifyHashedPassword(id, hash, password) == PasswordVerificationResult.Failed)
            {
                failures = until != "" ? 1 : failures + 1;
                Execute(
                    db,
                    "UPDATE Users SET Failures=$f,LockUntil=$l WHERE Id=$id",
                    ("$f", failures),
                    ("$l", failures >= 5 ? DateTimeOffset.UtcNow.AddMinutes(10).ToString("O") : ""),
                    ("$id", id));
                return null;
            }
            Execute(db, "UPDATE Users SET Failures=0,LockUntil='' WHERE Id=$id", ("$id", id));
            Log(db, name, "登录", login);
            return new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, name), new("stamp", stamp)], "Cookies"));
        }
    }

    /// <summary>
    /// 核验旧密码并原子更新密码哈希、安全戳及审计，要求用户重新登录。
    /// </summary>
    /// <param name="principal">调用方身份；服务层会重新验证安全戳及当前权限。</param>
    /// <param name="currentPassword">当前密码，用于确认修改者身份。</param>
    /// <param name="nextPassword">通过复杂度校验后生成新哈希的密码。</param>
    public void ChangePassword(ClaimsPrincipal principal, string currentPassword, string nextPassword)
    {
        lock (gate)
        {
            var actor = Require(principal, Permission.Read);
            ValidatePassword(nextPassword);
            using var db = Open();
            var hash = Scalar(db, "SELECT PasswordHash FROM Users WHERE Id=$id", ("$id", actor.Id));
            if (hasher.VerifyHashedPassword(actor.Id, hash, currentPassword) == PasswordVerificationResult.Failed)
            {
                throw new InvalidOperationException("当前密码不正确。");
            }

            using var tx = db.BeginTransaction();
            Execute(
                db,
                "UPDATE Users SET PasswordHash=$hash,Stamp=$stamp WHERE Id=$id",
                ("$hash", hasher.HashPassword(actor.Id, nextPassword)),
                ("$stamp", Guid.NewGuid().ToString()),
                ("$id", actor.Id));
            Log(db, actor.DisplayName, "修改密码", actor.Login);
            tx.Commit();
        }
    }
}
