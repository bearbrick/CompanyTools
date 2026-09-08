using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace DbStudio.Core;

public sealed partial class StudioStore
{
    /// <summary>列出本项目分享的状态，不存储可直接访问的明文令牌。</summary>
    public List<ProjectShare> Shares(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Manage);
        using var db = Open();
        using var cmd = Command(db, "SELECT Id,Name,CreatedAt,ExpiresAt,Revoked FROM ProjectShares WHERE ProjectId=$project ORDER BY CreatedAt DESC",
            ("$project", projectId));
        using var rows = cmd.ExecuteReader();
        var result = new List<ProjectShare>();
        while (rows.Read())
        {
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetBoolean(4)));
        }
        return result;
    }

    /// <summary>创建仅查看结构的随机能力链接。令牌只返回一次；项目管理和全局导出权限同时必需。</summary>
    public string CreateShare(ClaimsPrincipal principal, string projectId, string name, int validDays)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage | ProjectAccess.Export, Permission.Export);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || validDays is < 0 or > 365)
            {
                throw new InvalidOperationException("分享名称必填且最多 100 字符，有效期为 0～365 天（0 为长期有效）。");
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            using var db = Open();
            using var tx = db.BeginTransaction();
            var project = ReadProject(db, projectId);
            Execute(db, "INSERT INTO ProjectShares VALUES($id,$project,$name,$hash,$created,$expires,0)",
                ("$id", Guid.NewGuid().ToString("N")), ("$project", projectId), ("$name", name.Trim()),
                ("$hash", ShareHash(token)), ("$created", now.ToString("O")),
                ("$expires", validDays == 0 ? "" : now.AddDays(validDays).ToString("O")));
            Log(db, actor.DisplayName, "创建只读分享", $"{project.Name} / {name}", projectId);
            tx.Commit();
            return token;
        }
    }

    /// <summary>撤销链接，之后的页面请求立即失效；已经被访问者保存的内容无法收回。</summary>
    public void RevokeShare(ClaimsPrincipal principal, string projectId, string shareId)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage);
            using var db = Open();
            using var tx = db.BeginTransaction();
            Execute(db, "UPDATE ProjectShares SET Revoked=1 WHERE Id=$id AND ProjectId=$project", ("$id", shareId), ("$project", projectId));
            Log(db, actor.DisplayName, "撤销只读分享", "分享已撤销", projectId);
            tx.Commit();
        }
    }

    /// <summary>匿名只读入口：仅凭有效令牌读取单个项目的最新已保存结构，不提供任何写入能力。</summary>
    public DesignProject? SharedProject(string token)
    {
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
        {
            return null;
        }
        using var db = Open();
        var projectId = Scalar(db, """
            SELECT ProjectId FROM ProjectShares WHERE TokenHash=$hash AND Revoked=0
            AND (ExpiresAt='' OR ExpiresAt>$now)
            """, ("$hash", ShareHash(token)), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        return projectId == "" ? null : ReadProject(db, projectId);
    }

    private static string ShareHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
