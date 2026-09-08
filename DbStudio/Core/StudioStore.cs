using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace DbStudio.Core;

/// <summary>
/// 元数据应用服务。所有入口均按数据库中的当前角色授权，交互式会话不能绕过权限检查。
/// </summary>
public sealed partial class StudioStore
{
    private readonly string connectionString;
    private readonly PasswordHasher<string> hasher = new();
    private readonly object gate = new();

    /// <summary>
    /// 初始化本地存储；仅在空库中创建内置角色、首次管理员和示例项目。
    /// </summary>
    /// <param name="env">宿主环境，提供默认数据目录和种子文件路径。</param>
    /// <param name="config">可覆盖数据目录及首次管理员密码的配置。</param>
    public StudioStore(IWebHostEnvironment env, IConfiguration config)
    {
        var directory = config["Studio:DataDirectory"] ?? Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "studio.db"), ForeignKeys = true }.ToString();
        using var db = Open();
        Execute(db, "PRAGMA journal_mode=WAL;");
        Execute(
            db,
            """
            CREATE TABLE IF NOT EXISTS Roles (
                Id          TEXT PRIMARY KEY,
                Name        TEXT NOT NULL UNIQUE,
                Permissions INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Users (
                Id           TEXT PRIMARY KEY,
                Login        TEXT NOT NULL UNIQUE COLLATE NOCASE,
                DisplayName  TEXT NOT NULL,
                RoleId       TEXT NOT NULL REFERENCES Roles(Id),
                Enabled      INTEGER NOT NULL,
                PasswordHash TEXT NOT NULL,
                Stamp        TEXT NOT NULL,
                Failures     INTEGER NOT NULL DEFAULT 0,
                LockUntil    TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS Projects (
                Id       TEXT PRIMARY KEY,
                Name     TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                Document TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Audit (
                Id     INTEGER PRIMARY KEY AUTOINCREMENT,
                Time   TEXT NOT NULL,
                Actor  TEXT NOT NULL,
                Action TEXT NOT NULL,
                Detail TEXT NOT NULL
            );
            """);
        if (Scalar(db, "SELECT count(*) FROM Roles") == "0")
        {
            using var tx = db.BeginTransaction();
            Execute(db, "INSERT INTO Roles VALUES('admin','管理员',31),('designer','设计师',11),('reader','只读成员',1)");
            var password = config["Studio:InitialPassword"] ?? "Db!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9));
            Execute(
                db,
                "INSERT INTO Users(Id,Login,DisplayName,RoleId,Enabled,PasswordHash,Stamp) VALUES('admin','admin','系统管理员','admin',1,$hash,$stamp)",
                ("$hash", hasher.HashPassword("admin", password)),
                ("$stamp", Guid.NewGuid().ToString()));
            tx.Commit();
            File.WriteAllText(Path.Combine(directory, "first-run.txt"), $"DB Studio 首次运行账号\n用户名：admin\n密码：{password}\n登录后可在账号菜单修改密码。请妥善保存或删除此文件。\n");
        }
        InitializeProjectAccess(db);
        InitializeConnections(db, directory);
        if (Scalar(db, "SELECT count(*) FROM Projects") == "0")
        {
            var path = Path.Combine(env.ContentRootPath, "Seed", "panqi.json");
            var project = config.GetValue<bool>("Studio:LoadLegacySeed") && File.Exists(path)
                ? JsonSerializer.Deserialize<DesignProject>(File.ReadAllText(path), ModelJson.Options)!
                : new DesignProject { Name = "我的第一个项目" };
            ModelJson.NormalizeImport(project);
            project.Revision = 1;
            Execute(
                db,
                "INSERT INTO Projects VALUES($id,$name,1,$doc)",
                ("$id", project.Id),
                ("$name", project.Name),
                ("$doc", JsonSerializer.Serialize(project, ModelJson.Options)));
        }
    }

    /// <summary>
    /// 创建并打开独立连接；调用方使用 using 负责释放。
    /// </summary>
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    /// <summary>
    /// 使用命名参数构造 SQL 命令，业务输入不拼接到 SQL 语句中。
    /// </summary>
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    /// <summary>
    /// 在指定连接上执行写入；若连接已开启事务，则参与该事务。
    /// </summary>
    private static void Execute(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = Command(db, sql, args);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 读取单个标量值；无结果时返回空字符串。
    /// </summary>
    private static string Scalar(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = Command(db, sql, args);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    /// <summary>
    /// 以 UTC 时间写入审计，与调用方的业务事务一起提交或回滚。
    /// </summary>
    private static void Log(SqliteConnection db, string actor, string action, string detail, string projectId = "") => Execute(
                                                                                                   db,
                                                                                                   "INSERT INTO Audit(Time,Actor,Action,Detail,ProjectId) VALUES($time,$actor,$action,$detail,$project)",
                                                                                                   ("$time", DateTimeOffset.UtcNow.ToString("O")),
                                                                                                   ("$actor", actor),
                                                                                                   ("$action", action),
                                                                                                   ("$detail", detail),
                                                                                                   ("$project", projectId));
}
