using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DbStudio.Core;

/// <summary>
/// 负责版本存储结构的幂等迁移、修订写入和快照压缩。
/// </summary>
public sealed partial class StudioStore
{
    /// <summary>
    /// 幂等创建修订、发布和部署记录，并为旧项目补当前基线。
    /// </summary>
    private static void InitializeVersioning(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE IF NOT EXISTS ProjectRevisions (
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Revision INTEGER NOT NULL,
                Time TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Source TEXT NOT NULL,
                Action TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Document BLOB NOT NULL,
                RestoredFromRevision INTEGER NULL,
                PRIMARY KEY(ProjectId, Revision)
            );
            CREATE TABLE IF NOT EXISTS ProjectReleases (
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                Version INTEGER NOT NULL,
                SourceRevision INTEGER NOT NULL,
                Time TEXT NOT NULL,
                Actor TEXT NOT NULL,
                DocumentHash TEXT NOT NULL,
                PRIMARY KEY(ProjectId, Version),
                UNIQUE(ProjectId, SourceRevision)
            );
            CREATE TABLE IF NOT EXISTS DatabaseDeployments (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
                ConnectionId TEXT NOT NULL,
                ConnectionName TEXT NOT NULL,
                Server TEXT NOT NULL,
                DatabaseName TEXT NOT NULL,
                ProjectRevision INTEGER NOT NULL,
                Scope TEXT NOT NULL,
                Status TEXT NOT NULL,
                ReleaseVersion INTEGER NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Detail TEXT NOT NULL,
                Environment TEXT NOT NULL DEFAULT 'development',
                RequestedReleaseVersion INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS IX_DatabaseDeployments_ProjectConnection
                ON DatabaseDeployments(ProjectId, ConnectionId, StartedAt DESC);
            """);

        if (Convert.ToInt32(Scalar(db, "SELECT count(*) FROM pragma_table_info('DatabaseDeployments') WHERE name='Environment'")) == 0)
        {
            Execute(db, "ALTER TABLE DatabaseDeployments ADD COLUMN Environment TEXT NOT NULL DEFAULT 'development'");
        }
        if (Convert.ToInt32(Scalar(db, "SELECT count(*) FROM pragma_table_info('DatabaseDeployments') WHERE name='RequestedReleaseVersion'")) == 0)
        {
            Execute(db, "ALTER TABLE DatabaseDeployments ADD COLUMN RequestedReleaseVersion INTEGER NULL");
        }

        using var cmd = Command(db, "SELECT Id,Revision,Document FROM Projects");
        using var rows = cmd.ExecuteReader();
        var projects = new List<(string Id, int Revision, string Document)>();
        while (rows.Read())
        {
            projects.Add((rows.GetString(0), rows.GetInt32(1), rows.GetString(2)));
        }
        rows.Close();

        foreach (var project in projects)
        {
            Execute(db, """
                INSERT OR IGNORE INTO ProjectRevisions(ProjectId,Revision,Time,Actor,Source,Action,Summary,Document)
                VALUES($project,$revision,$time,'系统','baseline','建立历史基线','升级前的当前已保存设计',$document)
                """, ("$project", project.Id), ("$revision", project.Revision),
                ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$document", CompressDocument(project.Document)));
        }
    }

    /// <summary>
    /// 在项目文档提交的同一 SQLite 事务中写入完整修订快照。
    /// </summary>
    private static void RecordRevision(SqliteConnection db, DesignProject project, RevisionCommitInfo info)
        => Execute(db, """
            INSERT INTO ProjectRevisions(ProjectId,Revision,Time,Actor,Source,Action,Summary,Document,RestoredFromRevision)
            VALUES($project,$revision,$time,$actor,$source,$action,$summary,$document,$restored)
            """, ("$project", project.Id), ("$revision", project.Revision), ("$time", DateTimeOffset.UtcNow.ToString("O")),
            ("$actor", info.Actor), ("$source", info.Source), ("$action", info.Action), ("$summary", info.Summary),
            ("$document", CompressDocument(JsonSerializer.Serialize(project, ModelJson.Options))),
            ("$restored", (object?)info.RestoredFromRevision ?? DBNull.Value));

    /// <summary>
    /// 为新建或导入的项目记录 r1 快照。
    /// </summary>
    private static void RecordInitialRevision(SqliteConnection db, DesignProject project, string actor, string action, string summary)
        => RecordRevision(db, project, new(actor, "create", action, summary));

    /// <summary>
    /// 使用 Brotli 压缩完整 JSON，降低大项目多修订的 SQLite 体积。
    /// </summary>
    private static byte[] CompressDocument(string json)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }
        return output.ToArray();
    }

    /// <summary>
    /// 解压历史快照；文本格式的早期快照由读取层兼容。
    /// </summary>
    private static string DecompressDocument(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var stream = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
