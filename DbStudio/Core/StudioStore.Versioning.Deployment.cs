using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbStudio.Core;

/// <summary>
/// 负责数据库部署记录与不可变 V 发布版本的建立。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 在进入不可重复部署阶段前记录目标、范围和设计修订。
    /// </summary>
    internal string BeginDeployment(string actor, string projectId, DatabaseConnection connection, int revision, string scope)
    {
        var id = Guid.NewGuid().ToString("N");
        using var db = Open();
        Execute(db, """
            INSERT INTO DatabaseDeployments VALUES($id,$project,$connection,$name,$server,$database,$revision,$scope,'running',NULL,$time,'',$actor,'')
            """, ("$id", id), ("$project", projectId), ("$connection", connection.Id), ("$name", connection.Name),
            ("$server", connection.Server), ("$database", connection.Database), ("$revision", revision), ("$scope", scope),
            ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$actor", actor));
        return id;
    }

    /// <summary>
    /// 完成部署记录；仅在项目管理范围的整库校验通过时创建或复用 V。
    /// </summary>
    internal DatabaseDeploymentResult CompleteDeployment(string id, DesignProject project, string actor, bool fullyVerified, string detail)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            int? release = fullyVerified ? GetOrCreateRelease(db, project, actor) : null;
            Execute(db, "UPDATE DatabaseDeployments SET Status='succeeded',ReleaseVersion=$release,CompletedAt=$time,Detail=$detail WHERE Id=$id",
                ("$release", (object?)release ?? DBNull.Value), ("$time", DateTimeOffset.UtcNow.ToString("O")),
                ("$detail", detail), ("$id", id));
            tx.Commit();
            var message = fullyVerified
                ? $"项目管理范围内的整库验证通过，已发布 V{release}（设计 r{project.Revision}）。"
                : "局部同步成功，但目标库尚未与当前整个项目一致，未产生新 V 版本。";
            return new(fullyVerified, release, project.Revision, message);
        }
    }

    /// <summary>
    /// 记录已进入部署阶段后的失败，便于区分未执行计划和执行失败。
    /// </summary>
    internal void FailDeployment(string id, string detail)
    {
        using var db = Open();
        Execute(db, "UPDATE DatabaseDeployments SET Status='failed',CompletedAt=$time,Detail=$detail WHERE Id=$id",
            ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$detail", detail), ("$id", id));
    }

    /// <summary>
    /// 读取每个当前连接的最近执行状态与最后成功 r/V。
    /// </summary>
    public IReadOnlyList<DatabaseVersionStatus> DatabaseVersions(ClaimsPrincipal principal, string projectId)
    {
        RequireProject(principal, projectId, ProjectAccess.Database);
        using var db = Open();
        using var cmd = Command(db, """
            SELECT c.Id,c.Document,a.Status,s.ProjectRevision,s.ReleaseVersion,s.Scope,s.StartedAt,a.CompletedAt,a.Detail,
                   s.Server,s.DatabaseName,a.Server,a.DatabaseName
            FROM DatabaseConnections c
            LEFT JOIN DatabaseDeployments a ON a.Id=(SELECT Id FROM DatabaseDeployments x WHERE x.ProjectId=c.ProjectId AND x.ConnectionId=c.Id ORDER BY x.StartedAt DESC LIMIT 1)
            LEFT JOIN DatabaseDeployments s ON s.Id=(SELECT Id FROM DatabaseDeployments x WHERE x.ProjectId=c.ProjectId AND x.ConnectionId=c.Id AND x.Status='succeeded' ORDER BY x.StartedAt DESC LIMIT 1)
            WHERE c.ProjectId=$project ORDER BY c.rowid
            """, ("$project", projectId));
        using var rows = cmd.ExecuteReader();
        var result = new List<DatabaseVersionStatus>();
        while (rows.Read())
        {
            var profile = JsonSerializer.Deserialize<DatabaseConnection>(rows.GetString(1), ModelJson.Options)!;
            var successfulTargetMatches = TargetMatches(rows, 9, profile);
            var latestTargetMatches = TargetMatches(rows, 11, profile);
            result.Add(new(profile.Id, profile.Name, profile.Server, profile.Database,
                latestTargetMatches ? rows.GetString(2) : "never",
                successfulTargetMatches ? rows.GetInt32(3) : 0,
                successfulTargetMatches && !rows.IsDBNull(4) ? rows.GetInt32(4) : null,
                successfulTargetMatches ? rows.GetString(5) : "",
                successfulTargetMatches ? rows.GetString(6) : "",
                latestTargetMatches ? rows.GetString(7) : "",
                latestTargetMatches ? rows.GetString(8) : ""));
        }
        return result;
    }

    /// <summary>
    /// 同一设计修订只建立一个全局 V，同步到其他数据库时复用它。
    /// </summary>
    private static int GetOrCreateRelease(Microsoft.Data.Sqlite.SqliteConnection db, DesignProject project, string actor)
    {
        var existing = Scalar(db, "SELECT Version FROM ProjectReleases WHERE ProjectId=$project AND SourceRevision=$revision",
            ("$project", project.Id), ("$revision", project.Revision));
        if (int.TryParse(existing, out var number))
        {
            return number;
        }

        var version = int.Parse(Scalar(db, "SELECT COALESCE(MAX(Version),0)+1 FROM ProjectReleases WHERE ProjectId=$project",
            ("$project", project.Id)));
        Execute(db, "INSERT INTO ProjectReleases VALUES($project,$version,$revision,$time,$actor,$hash)",
            ("$project", project.Id), ("$version", version), ("$revision", project.Revision),
            ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$actor", actor), ("$hash", DocumentHash(project)));
        return version;
    }

    /// <summary>
    /// 连接配置更换服务器或数据库后，不把旧目标的同步状态冒充为当前状态。
    /// </summary>
    private static bool TargetMatches(Microsoft.Data.Sqlite.SqliteDataReader rows, int serverOrdinal, DatabaseConnection profile)
        => !rows.IsDBNull(serverOrdinal)
            && rows.GetString(serverOrdinal).Equals(profile.Server, StringComparison.OrdinalIgnoreCase)
            && rows.GetString(serverOrdinal + 1).Equals(profile.Database, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 使用规范 JSON 生成已发布快照指纹。
    /// </summary>
    private static string DocumentHash(DesignProject project)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(project, ModelJson.Options))));
}
