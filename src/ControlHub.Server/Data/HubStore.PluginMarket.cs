using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 插件市场代理缓存与插件下发的数据访问（#121）。
/// <para>A 端从 ClassIsland 官方插件索引同步元数据（<c>plugin_market_index</c>），
/// 把 <c>.cipx</c> 缓存到本地（<c>plugin_packages</c>），
/// 再通过现有指令通道下发给教室端；每批下发记一条 <c>plugin_deployments</c>，
/// 逐台进度直接复用 <c>device_commands</c>（多一个 <c>deployment_id</c> 列做归属）。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>索引条目的字段清单。</summary>
    private const string MarketColumns =
        "plugin_id, name, version, api_version, author, description, download_url, download_md5, "
        + "icon_path, readme_path, repo_url, supported_os, download_count, stars_count, synced_at";

    /// <summary>包缓存的字段清单。</summary>
    private const string PackageColumns =
        "plugin_id, version, file_path, file_name, sha256, md5, size_bytes, source_mirror, source_url, cached_at";

    private static PluginMarketRow ReadMarket(SqliteDataReader reader) => new()
    {
        PluginId = GetString(reader, "plugin_id"),
        Name = GetString(reader, "name"),
        Version = GetString(reader, "version"),
        ApiVersion = GetString(reader, "api_version"),
        Author = GetString(reader, "author"),
        Description = GetString(reader, "description"),
        DownloadUrl = GetString(reader, "download_url"),
        DownloadMd5 = GetString(reader, "download_md5"),
        IconPath = GetString(reader, "icon_path"),
        ReadmePath = GetString(reader, "readme_path"),
        RepoUrl = GetString(reader, "repo_url"),
        SupportedOs = GetString(reader, "supported_os"),
        DownloadCount = GetInt64(reader, "download_count"),
        StarsCount = GetInt64(reader, "stars_count"),
        SyncedAt = GetTimestampOrNow(reader, "synced_at"),
    };

    private static PluginPackageRow ReadPackage(SqliteDataReader reader) => new()
    {
        PluginId = GetString(reader, "plugin_id"),
        Version = GetString(reader, "version"),
        FilePath = GetString(reader, "file_path"),
        FileName = GetString(reader, "file_name"),
        Sha256 = GetString(reader, "sha256"),
        Md5 = GetString(reader, "md5"),
        SizeBytes = GetInt64(reader, "size_bytes"),
        SourceMirror = GetString(reader, "source_mirror"),
        SourceUrl = GetString(reader, "source_url"),
        CachedAt = GetTimestampOrNow(reader, "cached_at"),
    };

    /// <summary>建立插件市场相关的表（幂等）。</summary>
    private static async Task EnsurePluginMarketSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var market = connection.CreateCommand())
        {
            market.CommandText = """
                CREATE TABLE IF NOT EXISTS plugin_market_index (
                    plugin_id      TEXT PRIMARY KEY,
                    name           TEXT NOT NULL DEFAULT '',
                    version        TEXT NOT NULL DEFAULT '',
                    api_version    TEXT NOT NULL DEFAULT '',
                    author         TEXT NOT NULL DEFAULT '',
                    description    TEXT NOT NULL DEFAULT '',
                    download_url   TEXT NOT NULL DEFAULT '',
                    download_md5   TEXT NOT NULL DEFAULT '',
                    icon_path      TEXT NOT NULL DEFAULT '',
                    readme_path    TEXT NOT NULL DEFAULT '',
                    repo_url       TEXT NOT NULL DEFAULT '',
                    supported_os   TEXT NOT NULL DEFAULT '[]',
                    download_count INTEGER NOT NULL DEFAULT 0,
                    stars_count    INTEGER NOT NULL DEFAULT 0,
                    synced_at      TEXT NOT NULL
                );
                """;
            await market.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var packages = connection.CreateCommand())
        {
            // 一个插件可能同时缓存多个版本（索引升版后旧版仍留着）——主键用 (plugin_id, version)。
            packages.CommandText = """
                CREATE TABLE IF NOT EXISTS plugin_packages (
                    plugin_id     TEXT NOT NULL,
                    version       TEXT NOT NULL,
                    file_path     TEXT NOT NULL,
                    file_name     TEXT NOT NULL DEFAULT '',
                    sha256        TEXT NOT NULL DEFAULT '',
                    md5           TEXT NOT NULL DEFAULT '',
                    size_bytes    INTEGER NOT NULL DEFAULT 0,
                    source_mirror TEXT NOT NULL DEFAULT '',
                    source_url    TEXT NOT NULL DEFAULT '',
                    cached_at     TEXT NOT NULL,
                    PRIMARY KEY (plugin_id, version)
                );
                """;
            await packages.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deployments = connection.CreateCommand())
        {
            // 逐台进度不另建表：直接复用 device_commands（status / error_code / output 都是现成的），
            // 只是给指令多一个 deployment_id 归属列。少一套进度状态机，就少一处不一致的可能。
            deployments.CommandText = """
                CREATE TABLE IF NOT EXISTS plugin_deployments (
                    id             TEXT PRIMARY KEY,
                    plugin_id      TEXT NOT NULL,
                    plugin_name    TEXT NOT NULL DEFAULT '',
                    version        TEXT NOT NULL DEFAULT '',
                    action         TEXT NOT NULL DEFAULT 'install',
                    target_summary TEXT NOT NULL DEFAULT '',
                    created_by     TEXT NOT NULL DEFAULT '',
                    created_at     TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_plugin_deployments_created
                    ON plugin_deployments(created_at DESC);
                """;
            await deployments.ExecuteNonQueryAsync(cancellationToken);
        }

        // 归属列：老库补上（新库这里也走一次，EnsureColumnAsync 幂等）。
        await EnsureColumnAsync(connection, "device_commands", "deployment_id", "TEXT", cancellationToken);
        await using (var index = connection.CreateCommand())
        {
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_device_commands_deployment "
                + "ON device_commands(deployment_id);";
            await index.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // ────────────────────────────── 索引缓存 ──────────────────────────────

    /// <summary>
    /// 用刚同步到的索引整表替换本地缓存。
    /// <para>整体替换而不是逐条 upsert：索引被下架 / 更名的插件若不删掉，管理端会一直展示
    /// 「缓存了但装不上」的条目。索引只有几十条，重建代价可以忽略。</para>
    /// </summary>
    public async Task ReplaceMarketIndexAsync(IReadOnlyList<PluginMarketRow> rows,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM plugin_market_index;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = $"""
                INSERT INTO plugin_market_index ({MarketColumns})
                VALUES ($pluginId, $name, $version, $apiVersion, $author, $description, $downloadUrl,
                        $downloadMd5, $iconPath, $readmePath, $repoUrl, $supportedOs, $downloadCount,
                        $starsCount, $syncedAt);
                """;
            AddParameters(insert,
                ("$pluginId", row.PluginId),
                ("$name", row.Name),
                ("$version", row.Version),
                ("$apiVersion", row.ApiVersion),
                ("$author", row.Author),
                ("$description", row.Description),
                ("$downloadUrl", row.DownloadUrl),
                ("$downloadMd5", row.DownloadMd5),
                ("$iconPath", row.IconPath),
                ("$readmePath", row.ReadmePath),
                ("$repoUrl", row.RepoUrl),
                ("$supportedOs", row.SupportedOs),
                ("$downloadCount", row.DownloadCount),
                ("$starsCount", row.StarsCount),
                ("$syncedAt", Ts(row.SyncedAt)));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>读取全部索引条目（按下载量降序：常用插件排在前面）。</summary>
    public async Task<List<PluginMarketRow>> ListMarketIndexAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<PluginMarketRow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {MarketColumns} FROM plugin_market_index ORDER BY download_count DESC, plugin_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadMarket(reader));
        }

        return rows;
    }

    /// <summary>读取单个索引条目。</summary>
    public async Task<PluginMarketRow?> GetMarketItemAsync(string pluginId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MarketColumns} FROM plugin_market_index WHERE plugin_id = $id;";
        command.Parameters.AddWithValue("$id", pluginId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMarket(reader) : null;
    }

    // ────────────────────────────── 包缓存 ──────────────────────────────

    /// <summary>写入 / 覆盖一条包缓存记录。</summary>
    public async Task UpsertPackageAsync(PluginPackageRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO plugin_packages ({PackageColumns})
            VALUES ($pluginId, $version, $filePath, $fileName, $sha256, $md5, $sizeBytes, $sourceMirror,
                    $sourceUrl, $cachedAt)
            ON CONFLICT(plugin_id, version) DO UPDATE SET
                file_path = excluded.file_path,
                file_name = excluded.file_name,
                sha256 = excluded.sha256,
                md5 = excluded.md5,
                size_bytes = excluded.size_bytes,
                source_mirror = excluded.source_mirror,
                source_url = excluded.source_url,
                cached_at = excluded.cached_at;
            """;
        AddParameters(command,
            ("$pluginId", row.PluginId),
            ("$version", row.Version),
            ("$filePath", row.FilePath),
            ("$fileName", row.FileName),
            ("$sha256", row.Sha256),
            ("$md5", row.Md5),
            ("$sizeBytes", row.SizeBytes),
            ("$sourceMirror", row.SourceMirror),
            ("$sourceUrl", row.SourceUrl),
            ("$cachedAt", Ts(row.CachedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取一条包缓存记录。</summary>
    public async Task<PluginPackageRow?> GetPackageAsync(string pluginId, string version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {PackageColumns} FROM plugin_packages WHERE plugin_id = $id AND version = $version;";
        command.Parameters.AddWithValue("$id", pluginId);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPackage(reader) : null;
    }

    /// <summary>读取全部包缓存记录。</summary>
    public async Task<List<PluginPackageRow>> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<PluginPackageRow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {PackageColumns} FROM plugin_packages ORDER BY cached_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadPackage(reader));
        }

        return rows;
    }

    /// <summary>删除一条包缓存记录。</summary>
    public async Task<bool> DeletePackageAsync(string pluginId, string version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM plugin_packages WHERE plugin_id = $id AND version = $version;";
        command.Parameters.AddWithValue("$id", pluginId);
        command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // ────────────────────────────── 下发记录 ──────────────────────────────

    /// <summary>创建一条下发记录。</summary>
    public async Task CreateDeploymentAsync(PluginDeploymentRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_deployments
                (id, plugin_id, plugin_name, version, action, target_summary, created_by, created_at)
            VALUES ($id, $pluginId, $pluginName, $version, $action, $targetSummary, $createdBy, $createdAt);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$pluginId", row.PluginId),
            ("$pluginName", row.PluginName),
            ("$version", row.Version),
            ("$action", row.Action),
            ("$targetSummary", row.TargetSummary),
            ("$createdBy", row.CreatedBy),
            ("$createdAt", Ts(row.CreatedAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>最近的下发记录（不含逐台明细）。</summary>
    public async Task<List<PluginDeploymentRow>> ListDeploymentsAsync(int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PluginDeploymentRow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, plugin_id, plugin_name, version, action, target_summary, created_by, created_at
            FROM plugin_deployments ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PluginDeploymentRow
            {
                Id = GetString(reader, "id"),
                PluginId = GetString(reader, "plugin_id"),
                PluginName = GetString(reader, "plugin_name"),
                Version = GetString(reader, "version"),
                Action = GetString(reader, "action"),
                TargetSummary = GetString(reader, "target_summary"),
                CreatedBy = GetString(reader, "created_by"),
                CreatedAt = GetTimestampOrNow(reader, "created_at"),
            });
        }

        return rows;
    }

    /// <summary>读取一条下发记录。</summary>
    public async Task<PluginDeploymentRow?> GetDeploymentAsync(string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, plugin_id, plugin_name, version, action, target_summary, created_by, created_at
            FROM plugin_deployments WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PluginDeploymentRow
        {
            Id = GetString(reader, "id"),
            PluginId = GetString(reader, "plugin_id"),
            PluginName = GetString(reader, "plugin_name"),
            Version = GetString(reader, "version"),
            Action = GetString(reader, "action"),
            TargetSummary = GetString(reader, "target_summary"),
            CreatedBy = GetString(reader, "created_by"),
            CreatedAt = GetTimestampOrNow(reader, "created_at"),
        };
    }

    /// <summary>某次下发对应的逐台指令（含设备名，供进度面板直接渲染）。</summary>
    public async Task<List<PluginDeploymentItemRow>> ListDeploymentItemsAsync(string deploymentId,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PluginDeploymentItemRow>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id AS command_id, c.device_id AS device_id, c.status AS status,
                   c.error_code AS error_code, c.output AS output, c.finished_at AS finished_at,
                   COALESCE(d.name, c.device_id) AS device_name
            FROM device_commands c
            LEFT JOIN devices d ON d.id = c.device_id
            WHERE c.deployment_id = $deploymentId
            ORDER BY device_name;
            """;
        command.Parameters.AddWithValue("$deploymentId", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PluginDeploymentItemRow
            {
                CommandId = GetString(reader, "command_id"),
                DeviceId = GetString(reader, "device_id"),
                DeviceName = GetString(reader, "device_name"),
                Status = GetString(reader, "status"),
                ErrorCode = GetString(reader, "error_code"),
                Output = GetString(reader, "output"),
                FinishedAt = GetTimestamp(reader, "finished_at"),
            });
        }

        return rows;
    }
}

/// <summary>插件索引缓存行。</summary>
public sealed class PluginMarketRow
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>索引中的版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>要求的最低宿主 API 版本（字符串）。</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>作者。</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>下载地址模板（含 <c>{root}</c>）。</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>索引给出的 MD5。</summary>
    public string DownloadMd5 { get; set; } = string.Empty;

    /// <summary>图标路径模板（含 <c>{root}</c>）。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>自述文件路径模板（含 <c>{root}</c>）。</summary>
    public string ReadmePath { get; set; } = string.Empty;

    /// <summary>仓库地址。</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>支持平台（JSON 数组文本）。</summary>
    public string SupportedOs { get; set; } = "[]";

    /// <summary>下载量。</summary>
    public long DownloadCount { get; set; }

    /// <summary>星标数。</summary>
    public long StarsCount { get; set; }

    /// <summary>同步时间。</summary>
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>插件包缓存行。</summary>
public sealed class PluginPackageRow
{
    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>本地绝对路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA256（发出去给教室端校验的就是它）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>索引给出的 MD5（下载后即刻校验上游完整性）。</summary>
    public string Md5 { get; set; } = string.Empty;

    /// <summary>字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>实际下载用的镜像名。</summary>
    public string SourceMirror { get; set; } = string.Empty;

    /// <summary>实际下载地址。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>缓存时间。</summary>
    public DateTimeOffset CachedAt { get; set; }
}

/// <summary>插件下发记录行。</summary>
public sealed class PluginDeploymentRow
{
    /// <summary>记录 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>插件 ID。</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>插件名（下发时的快照，索引之后改名也不影响历史）。</summary>
    public string PluginName { get; set; } = string.Empty;

    /// <summary>版本。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>动作。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>目标描述。</summary>
    public string TargetSummary { get; set; } = string.Empty;

    /// <summary>发起人。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>发起时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>下发记录里的逐台指令行。</summary>
public sealed class PluginDeploymentItemRow
{
    /// <summary>指令 ID。</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>设备 ID。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>指令状态。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>失败分类。</summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>输出 / 错误原文。</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>结束时间。</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}
