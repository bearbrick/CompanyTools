using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DbStudio.Core;

/// <summary>
/// 提供修订查询、差异和新修订式恢复，不允许覆盖已有历史。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 读取项目全部修订和已发布版本；旧项目仅能从升级时基线开始。
    /// </summary>
    public ProjectVersionSummary Versions(ClaimsPrincipal principal, string projectId, int revisionLimit = 100)
    {
        RequireProject(principal, projectId, ProjectAccess.Read);
        if (revisionLimit is < 1 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(revisionLimit), "每次可读取 1 至 2000 条修订记录。");
        }
        using var db = Open();
        var current = ReadProject(db, projectId);
        var releases = ReadReleases(db, projectId);
        var total = int.Parse(Scalar(db, "SELECT COUNT(*) FROM ProjectRevisions WHERE ProjectId=$project", ("$project", projectId)));
        var revisions = ReadRevisions(db, projectId, revisionLimit);
        return new(current.Revision, releases.FirstOrDefault()?.Version ?? 0,
            releases.FirstOrDefault()?.SourceRevision, total, revisions, releases);
    }

    /// <summary>
    /// 比较任意历史修订与另一修订；目标留空时使用当前设计。
    /// </summary>
    public ProjectRevisionDiff RevisionDiff(ClaimsPrincipal principal, string projectId, int fromRevision, int? toRevision = null, string? tableId = null)
    {
        RequireProject(principal, projectId, ProjectAccess.Read);
        using var db = Open();
        var before = ReadRevision(db, projectId, fromRevision);
        var after = toRevision.HasValue ? ReadRevision(db, projectId, toRevision.Value) : ReadProject(db, projectId);
        return ProjectVersionDiffEngine.Compare(before, after, tableId);
    }

    /// <summary>
    /// 把历史整体恢复为一次新修订，不回退修订号或已发布版本。
    /// </summary>
    public DesignProject RestoreProjectRevision(ClaimsPrincipal principal, string projectId, int expectedRevision, int sourceRevision)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Manage, Permission.Design);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var current = ReadProject(db, projectId);
            EnsureCurrentRevision(current, expectedRevision);
            var historical = ReadRevision(db, projectId, sourceRevision);
            historical.Id = current.Id;
            historical.Revision = current.Revision;
            if (CommitProject(db, historical, expectedRevision,
                new(actor.DisplayName, "restore", "恢复整个项目", $"从 r{sourceRevision} 恢复", sourceRevision)))
            {
                Log(db, actor.DisplayName, "恢复整个项目", $"r{sourceRevision} → r{historical.Revision}", projectId);
            }
            tx.Commit();
            return historical;
        }
    }

    /// <summary>
    /// 只恢复历史中的一张表，其他当前设计保持不变。
    /// </summary>
    public DesignProject RestoreTableRevision(ClaimsPrincipal principal, string projectId, int expectedRevision, int sourceRevision, string tableId)
    {
        lock (gate)
        {
            var actor = RequireProject(principal, projectId, ProjectAccess.Design, Permission.Design);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var current = ReadProject(db, projectId);
            EnsureCurrentRevision(current, expectedRevision);
            var historical = ReadRevision(db, projectId, sourceRevision);
            var source = historical.Tables.FirstOrDefault(table => table.Id == tableId)
                ?? throw new InvalidOperationException("该历史修订中还没有这张表。");
            var index = current.Tables.FindIndex(table => table.Id == tableId);
            if (index < 0)
            {
                current.Tables.Add(ModelJson.Clone(source));
            }
            else
            {
                current.Tables[index] = ModelJson.Clone(source);
            }

            var errors = DesignValidation.Check(current, current.Tables.First(table => table.Id == source.Id)).Distinct().ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("恢复后的关联结构不完整：\n" + string.Join("\n", errors));
            }
            if (CommitProject(db, current, expectedRevision,
                new(actor.DisplayName, "restore", "恢复数据表", $"{source.Schema}.{source.Name} · 来自 r{sourceRevision}", sourceRevision)))
            {
                Log(db, actor.DisplayName, "恢复数据表", $"{source.Schema}.{source.Name} · r{sourceRevision} → r{current.Revision}", projectId);
            }
            tx.Commit();
            return current;
        }
    }

    /// <summary>
    /// 读取已发布版本，按 V 倒序返回。
    /// </summary>
    private static List<ProjectReleaseInfo> ReadReleases(SqliteConnection db, string projectId)
    {
        var result = new List<ProjectReleaseInfo>();
        using var cmd = Command(db, "SELECT Version,SourceRevision,Time,Actor,DocumentHash FROM ProjectReleases WHERE ProjectId=$project ORDER BY Version DESC", ("$project", projectId));
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            result.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetString(2), rows.GetString(3), rows.GetString(4)));
        }
        return result;
    }

    /// <summary>
    /// 读取项目修订元数据，不向前端暴露快照 JSON。
    /// </summary>
    private static List<ProjectRevisionInfo> ReadRevisions(SqliteConnection db, string projectId, int limit)
    {
        var result = new List<ProjectRevisionInfo>();
        using var cmd = Command(db, """
            SELECT r.Revision,r.Time,r.Actor,r.Source,r.Action,r.Summary,p.Version,r.RestoredFromRevision
            FROM ProjectRevisions r LEFT JOIN ProjectReleases p ON p.ProjectId=r.ProjectId AND p.SourceRevision=r.Revision
            WHERE r.ProjectId=$project ORDER BY r.Revision DESC LIMIT $limit
            """, ("$project", projectId), ("$limit", limit));
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            result.Add(new(rows.GetInt32(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4),
                rows.GetString(5), rows.IsDBNull(6) ? null : rows.GetInt32(6), rows.IsDBNull(7) ? null : rows.GetInt32(7)));
        }
        return result;
    }

    /// <summary>
    /// 读取并归一化一个历史快照，兼容早期未压缩的文本记录。
    /// </summary>
    private static DesignProject ReadRevision(SqliteConnection db, string projectId, int revision)
    {
        using var cmd = Command(db, "SELECT Document FROM ProjectRevisions WHERE ProjectId=$project AND Revision=$revision",
            ("$project", projectId), ("$revision", revision));
        var stored = cmd.ExecuteScalar();
        if (stored == null || stored == DBNull.Value)
        {
            throw new InvalidOperationException("历史修订不存在。升级前的旧修订无法反向重建。");
        }
        var json = stored is byte[] bytes ? DecompressDocument(bytes) : Convert.ToString(stored) ?? "";
        return ModelJson.NormalizeImport(JsonSerializer.Deserialize<DesignProject>(json, ModelJson.Options)!);
    }

    /// <summary>
    /// 恢复前再次验证乐观并发修订号。
    /// </summary>
    private static void EnsureCurrentRevision(DesignProject project, int expectedRevision)
    {
        if (project.Revision != expectedRevision)
        {
            throw new InvalidOperationException("项目已更新，请刷新后重新预览恢复内容。");
        }
    }
}
