using System.Security.Cryptography;
using System.Text;
using ControlHub.Protocol;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>API 密钥的一行（**不含明文**：库里只存哈希与前缀）。</summary>
public sealed class ApiKeyRow
{
    /// <summary>密钥 ID（用于撤销与列表展示）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>给人看的名字，例如「机房监控脚本」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>明文前若干字符，便于在列表里辨认是哪一把（不足以还原密钥）。</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>明文的 SHA-256 十六进制哈希。</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>该密钥被授予的权限集合（可只给只读）。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>创建者账号。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>过期时间（可空表示长期有效）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>最近使用时间（用于判断某把密钥是否还在被使用）。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>是否已撤销。</summary>
    public bool Revoked { get; set; }

    /// <summary>备注：用途说明，方便交接。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 开放 API 的密钥（#74）。
/// <para>设计取舍：**库里只存哈希**——密钥明文只在创建响应里出现一次，之后任何接口都取不回来；
/// 权限按「创建时选定的子集」授予，默认建议只给只读，避免脚本拿一把万能钥匙。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>密钥明文前缀（便于在日志与列表里一眼认出这是集控密钥）。</summary>
    public const string ApiKeyPrefix = "chk_";

    /// <summary>鉴权方案名：<c>Authorization: ApiKey chk_...</c>。</summary>
    public const string ApiKeyScheme = "ApiKey";

    private const string ApiKeyColumns =
        "id, name, prefix, key_hash, permissions, created_by, created_at, expires_at, last_used_at, revoked, note";

    private static ApiKeyRow ReadApiKey(SqliteDataReader reader) => new()
    {
        Id = GetString(reader, "id"),
        Name = GetString(reader, "name"),
        Prefix = GetString(reader, "prefix"),
        KeyHash = GetString(reader, "key_hash"),
        Permissions = PermissionKeys.Normalize(
            HubJson.DeserializeOrDefault<List<string>>(GetString(reader, "permissions"), [])),
        CreatedBy = GetString(reader, "created_by"),
        CreatedAt = GetTimestampOrNow(reader, "created_at"),
        ExpiresAt = GetTimestamp(reader, "expires_at"),
        LastUsedAt = GetTimestamp(reader, "last_used_at"),
        Revoked = GetBool(reader, "revoked"),
        Note = GetString(reader, "note"),
    };

    /// <summary>生成一把新密钥的明文（<c>chk_</c> + 32 字节 base64url）。</summary>
    public static string NewApiKey() => ApiKeyPrefix + HubChecksum.NewToken(32);

    /// <summary>明文 → 哈希。用 SHA-256 而非慢哈希：密钥本身是高熵随机串，不需要抗暴力破解。</summary>
    public static string HashApiKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>创建密钥（调用方负责生成明文与哈希）。</summary>
    public async Task CreateApiKeyAsync(ApiKeyRow row, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO api_keys ({ApiKeyColumns})
            VALUES ($id, $name, $prefix, $hash, $permissions, $createdBy, $createdAt, $expiresAt, NULL, 0, $note);
            """;
        AddParameters(command,
            ("$id", row.Id),
            ("$name", row.Name),
            ("$prefix", row.Prefix),
            ("$hash", row.KeyHash),
            ("$permissions", HubJson.Serialize(row.Permissions)),
            ("$createdBy", row.CreatedBy),
            ("$createdAt", Ts(DateTimeOffset.UtcNow)),
            ("$expiresAt", row.ExpiresAt.HasValue ? Ts(row.ExpiresAt.Value) : null),
            ("$note", row.Note ?? string.Empty));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>列出全部密钥（新的在前）。</summary>
    public async Task<List<ApiKeyRow>> GetApiKeysAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ApiKeyColumns} FROM api_keys ORDER BY created_at DESC;";

        var result = new List<ApiKeyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadApiKey(reader));
        }

        return result;
    }

    /// <summary>按哈希查密钥（鉴权入口）。</summary>
    public async Task<ApiKeyRow?> GetApiKeyByHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ApiKeyColumns} FROM api_keys WHERE key_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", keyHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadApiKey(reader) : null;
    }

    /// <summary>记录一次使用（供「这把密钥还在用吗」判断）。</summary>
    public async Task TouchApiKeyAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET last_used_at = $now WHERE id = $id;";
        AddParameters(command, ("$now", Ts(DateTimeOffset.UtcNow)), ("$id", id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>撤销密钥（保留记录，便于审计追溯）。</summary>
    public async Task<bool> RevokeApiKeyAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_keys SET revoked = 1 WHERE id = $id AND revoked = 0;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>彻底删除密钥记录。</summary>
    public async Task<bool> DeleteApiKeyAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_keys WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
