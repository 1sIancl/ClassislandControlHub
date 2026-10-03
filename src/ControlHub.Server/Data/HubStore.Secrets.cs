using ControlHub.Server.Services;
using Microsoft.Data.Sqlite;

namespace ControlHub.Server.Data;

/// <summary>
/// 静态敏感数据加密（#36，见 <c>docs/adr/0005-encrypt-secrets-at-rest.md</c>）：启动迁移与诊断。
/// <para>覆盖对象：设备注册码（<c>enroll_codes</c>）、管理端邀请码（<c>register_codes</c>）、
/// Webhook 加签密钥（<c>webhooks.secret</c>）。管理员密码与 API 密钥是哈希，不在范围内。</para>
/// <para><b>查找</b>：注册码类数据的 <c>code</c> 改为存密文后无法等值查找，因此新增确定性的
/// HMAC 指纹列 <c>code_hash</c> —— 见 ADR 0005「实现期修正」。</para>
/// </summary>
public sealed partial class HubStore
{
    /// <summary>
    /// 注册码 / 邀请码的规范化形式：去首尾空白 + 统一大写。
    /// 用户输入、指纹计算与写入都必须先经过它，否则同一码会因大小写不同而查不到。
    /// </summary>
    public static string NormalizeEnrollCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>指纹引用的前缀：形如 <c>ref:Ab3dEf9h</c>，见 <see cref="SecretReference"/>。</summary>
    public const string SecretReferencePrefix = "ref:";

    /// <summary>
    /// 注册码 / 邀请码的**指纹引用**：HMAC 指纹（<c>code_hash</c>）的前 8 个字符。
    /// <para>用途有二：① 审计日志等**非加密表**里引用某个码，不能落明文也不能用掩码
    /// （保留前 4 位会把未知位数从 8 位降到 4 位，配合在线爆破足以猜出注册码，等于给「拷走数据库」
    /// 提供旁路）；② 明文解密失败（密钥文件被更换 / 丢失）时，管理端仍能用引用删除 / 编辑该行。</para>
    /// <para>注意：引用必须取自**库中的 <c>code_hash</c>**，不能用当前密钥重新计算——
    /// 密钥更换后重算的结果与库里的旧指纹不一致，引用也就对不上了。</para>
    /// </summary>
    public static string SecretReference(string? codeHash)
    {
        if (string.IsNullOrWhiteSpace(codeHash))
        {
            return "(无)";
        }

        return SecretReferencePrefix + codeHash[..Math.Min(8, codeHash.Length)];
    }

    /// <summary>由**明文**注册码算指纹引用（仅用于刚写入、指纹就是当前密钥算出的场景）。</summary>
    public string ComputeCodeReference(string? code)
    {
        var plain = NormalizeEnrollCode(code);
        return plain.Length == 0 ? "(空)" : SecretReference(_secrets.ComputeLookupHash(plain));
    }

    /// <summary>
    /// 解析查找条件：接受「明文注册码」（规范化后按指纹查，兼容未迁移的明文老行）
    /// 或「指纹引用」<c>ref:xxxxxxxx</c>（供明文不可用时管理端操作该行）。
    /// <para>返回的 SQL 使用 <c>$hash</c> 与 <c>$plain</c> 两个参数，调用方用 <see cref="AddParameters"/>
    /// 传入返回的 Hash / Plain 值即可。</para>
    /// </summary>
    private (string Sql, string Hash, string Plain) ResolveSecretLookup(string? code)
    {
        var raw = (code ?? string.Empty).Trim();
        if (raw.StartsWith(SecretReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = raw[SecretReferencePrefix.Length..].Trim();
            return ("substr(code_hash, 1, 8) = $hash", reference, "\u0000");
        }

        var plain = NormalizeEnrollCode(raw);
        return (SecretLookupSql, _secrets.ComputeLookupHash(plain), plain);
    }

    /// <summary>写库前加密（幂等：已是密文则原样返回；空值保持空）。</summary>
    private string ProtectSecret(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? plaintext : _secrets.Protect(plaintext);

    /// <summary>
    /// 读库后解密。
    /// <para><paramref name="available"/> 为 false 表示这条密文解密失败（密钥文件被更换 / 丢失），
    /// 调用方必须按「该值不可用」处理，**绝不能退回明文或空值继续当成正常值使用**——
    /// 那会让加密变成摆设。空值与老库明文视为可用。</para>
    /// </summary>
    private string ReadSecret(string stored, out bool available)
    {
        if (string.IsNullOrEmpty(stored) || !SecretProtector.IsEncrypted(stored))
        {
            available = true;
            return stored;
        }

        if (_secrets.TryUnprotect(stored, out var plaintext, out _))
        {
            available = true;
            return plaintext ?? string.Empty;
        }

        available = false;
        return string.Empty;
    }

    /// <summary>
    /// 可查找敏感值的匹配条件：优先按 HMAC 指纹；对**尚未迁移**（指纹为空）的老行，
    /// 退化按规范化明文匹配——这样即使启动迁移失败，老注册码也不会全部失效。
    /// <para>需要同时提供 <c>$hash</c> 与 <c>$plain</c> 两个参数。</para>
    /// </summary>
    private const string SecretLookupSql =
        "(code_hash = $hash OR ((code_hash IS NULL OR code_hash = '') AND code = $plain))";

    /// <summary>把仍是明文的注册码 / 邀请码 / Webhook 密钥就地升级为密文，并补齐 HMAC 指纹。幂等。</summary>
    public async Task<SecretMigrationResult> MigrateSecretsAsync(CancellationToken cancellationToken = default)
    {
        var result = new SecretMigrationResult();
        await using var connection = await OpenAsync(cancellationToken);

        // ① 设备注册码：先补指纹（此时 code 还是明文，可直接算），再加密 code。
        await MigrateLookupSecretsAsync(connection, "enroll_codes", result,
            encrypted => result.EnrollCodesEncrypted += encrypted,
            fingerprinted => result.EnrollCodesFingerprinted += fingerprinted,
            error => result.Errors.Add("enroll_codes：" + error), cancellationToken);

        // ② 管理端邀请码：凭码可注册出管理账号，比设备注册码更敏感，同样处理。
        await MigrateLookupSecretsAsync(connection, "register_codes", result,
            encrypted => result.RegisterCodesEncrypted += encrypted,
            fingerprinted => result.RegisterCodesFingerprinted += fingerprinted,
            error => result.Errors.Add("register_codes：" + error), cancellationToken);

        // ③ Webhook 加签密钥：不做查找，直接加密（幂等）。
        var hooks = new List<(long RowId, string Secret)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT rowid, secret FROM webhooks;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hooks.Add((reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        foreach (var (rowId, secret) in hooks)
        {
            if (string.IsNullOrEmpty(secret) || SecretProtector.IsEncrypted(secret))
            {
                continue;
            }

            try
            {
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE webhooks SET secret = $secret WHERE rowid = $rowid;";
                update.Parameters.AddWithValue("$secret", _secrets.Protect(secret));
                update.Parameters.AddWithValue("$rowid", rowId);
                await update.ExecuteNonQueryAsync(cancellationToken);
                result.WebhooksEncrypted++;
            }
            catch (Exception ex)
            {
                // 单行失败只记录、不阻断启动（ADR 0005 决策）。
                result.Errors.Add("webhooks 加密失败：" + ex.Message);
            }
        }

        // ④ 兜底清洗：注册码类审计里那些**表内已不存在**的明文（早已删除的注册码）。
        // 它们已失效、无法再注册，但明文留在库里没有价值；统一替换为占位符。
        // 必须在 ①② 之后执行——那时表内仍在的注册码已经换成指纹引用，不会被这条误伤。
        result.AuditReferencesScrubbed += await ScrubOrphanAuditTargetsAsync(connection, cancellationToken);

        return result;
    }

    /// <summary>
    /// 兜底清洗注册码类审计记录里的「孤儿明文」：目标已不在 <c>enroll_codes</c> / <c>register_codes</c> 中
    /// （典型是早已删除），因此前面按指纹引用精确替换时覆盖不到它们。
    /// </summary>
    private static async Task<int> ScrubOrphanAuditTargetsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE audit_logs SET target = '(历史明文已清理)'
             WHERE target NOT LIKE 'ref:%'
               AND target <> '(历史明文已清理)'
               AND action IN ('enrollcode.create', 'enrollcode.update', 'enrollcode.delete',
                              'register-code.create', 'register-code.delete');
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>迁移一张「有 code / code_hash 两列」的表（设备注册码、管理端邀请码）。</summary>
    private async Task MigrateLookupSecretsAsync(SqliteConnection connection, string table,
        SecretMigrationResult result, Action<int> countEncrypted, Action<int> countFingerprinted,
        Action<string> addError, CancellationToken cancellationToken)
    {
        var rows = new List<(long RowId, string Code, string? Hash)>();
        await using (var select = connection.CreateCommand())
        {
            // 表名是内部常量（不是用户输入），这里拼接安全。
            select.CommandText = $"SELECT rowid, code, code_hash FROM {table};";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var (rowId, code, hash) in rows)
        {
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (SecretProtector.IsEncrypted(code))
            {
                // 已是密文：此时明文不可得，指纹缺失的行无法再补（该行将不可用），只计数。
                if (string.IsNullOrWhiteSpace(hash))
                {
                    result.Unrecoverable++;
                }

                continue;
            }

            try
            {
                var plain = NormalizeEnrollCode(code);
                var needHash = string.IsNullOrWhiteSpace(hash);
                var fingerprint = needHash ? _secrets.ComputeLookupHash(plain) : hash!;

                await using var update = connection.CreateCommand();
                update.CommandText =
                    $"UPDATE {table} SET code = $code, code_hash = $hash WHERE rowid = $rowid;";
                update.Parameters.AddWithValue("$code", _secrets.Protect(plain));
                update.Parameters.AddWithValue("$hash", fingerprint);
                update.Parameters.AddWithValue("$rowid", rowId);
                await update.ExecuteNonQueryAsync(cancellationToken);

                countEncrypted(1);
                if (needHash)
                {
                    countFingerprinted(1);
                }

                // 清洗历史审计里的明文（升级前的版本把 target 直接写成明文注册码）。
                // 不清理的话，「拷走数据库」可以从审计表直接拿到仍然有效的注册码，加密等于白做。
                result.AuditReferencesScrubbed += await ScrubAuditAsync(connection, plain,
                    SecretReference(fingerprint), cancellationToken);
            }
            catch (Exception ex)
            {
                // 单行失败只记录、不阻断启动；该行会走「按明文匹配」的兼容路径，仍可用。
                addError("加密失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 把历史审计记录里的注册码明文替换成指纹引用（只可能在启动迁移时做——那时明文还在手上）。
    /// <para>升级前的版本把注册码明文写进了 <c>audit_logs.target</c>（detail 里也可能出现），
    /// 不清洗就等于给「拷走数据库」留了一条绕过加密的旁路。</para>
    /// </summary>
    private static async Task<int> ScrubAuditAsync(SqliteConnection connection, string plain, string reference,
        CancellationToken cancellationToken)
    {
        var sql = "UPDATE audit_logs SET target = $reference WHERE target = $plain;";
        // detail 是自由文本：只对足够长的码做子串替换，避免误伤正常文本。
        if (plain.Length >= 6)
        {
            sql += "UPDATE audit_logs SET detail = replace(detail, $plain, $reference) "
                   + "WHERE detail LIKE '%' || $plain || '%';";
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$reference", reference);
        command.Parameters.AddWithValue("$plain", plain);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>统计加密覆盖率与不可用条数，供启动日志与 <c>/server/info</c> 核对「到底加密了没有」。</summary>
    public async Task<SecretEncryptionStats> GetSecretEncryptionStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return new SecretEncryptionStats
        {
            EnrollCodes = await CountSecretsAsync(connection, "enroll_codes", "code", cancellationToken),
            RegisterCodes = await CountSecretsAsync(connection, "register_codes", "code", cancellationToken),
            Webhooks = await CountSecretsAsync(connection, "webhooks", "secret", cancellationToken),
            KeyFile = _secrets.KeyPath,
            KeyGenerated = _secrets.KeyGenerated,
        };
    }

    private async Task<SecretKindStats> CountSecretsAsync(SqliteConnection connection, string table, string column,
        CancellationToken cancellationToken)
    {
        var stats = new SecretKindStats();
        await using var command = connection.CreateCommand();
        // 表名 / 列名均为内部常量，拼接安全。
        command.CommandText = $"SELECT {column} FROM {table};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stored = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            stats.Total++;
            if (!SecretProtector.IsEncrypted(stored))
            {
                continue;
            }

            stats.Encrypted++;
            if (!_secrets.TryUnprotect(stored, out _, out _))
            {
                stats.Unavailable++;
            }
        }

        return stats;
    }
}

/// <summary>一次启动迁移的统计结果（用于日志，不含任何敏感内容）。</summary>
public sealed class SecretMigrationResult
{
    /// <summary>设备注册码：本次加密的条数。</summary>
    public int EnrollCodesEncrypted { get; set; }

    /// <summary>设备注册码：本次补算指纹的条数。</summary>
    public int EnrollCodesFingerprinted { get; set; }

    /// <summary>管理端邀请码：本次加密的条数。</summary>
    public int RegisterCodesEncrypted { get; set; }

    /// <summary>管理端邀请码：本次补算指纹的条数。</summary>
    public int RegisterCodesFingerprinted { get; set; }

    /// <summary>Webhook 加签密钥：本次加密的条数。</summary>
    public int WebhooksEncrypted { get; set; }

    /// <summary>已是密文但缺少指纹、明文不可得因此无法修复的条数（这些行不可用）。</summary>
    public int Unrecoverable { get; set; }

    /// <summary>历史审计记录中被替换为指纹引用的明文注册码条数。</summary>
    public int AuditReferencesScrubbed { get; set; }

    /// <summary>单行失败的原因（不含敏感内容）。</summary>
    public List<string> Errors { get; } = [];

    /// <summary>本次是否有任何改动。</summary>
    public bool Changed => EnrollCodesEncrypted + RegisterCodesEncrypted + WebhooksEncrypted > 0;
}

/// <summary>静态敏感数据的加密覆盖率统计。</summary>
public sealed class SecretEncryptionStats
{
    /// <summary>设备注册码。</summary>
    public SecretKindStats EnrollCodes { get; set; } = new();

    /// <summary>管理端邀请码。</summary>
    public SecretKindStats RegisterCodes { get; set; } = new();

    /// <summary>Webhook 加签密钥。</summary>
    public SecretKindStats Webhooks { get; set; } = new();

    /// <summary>密钥文件路径（现场最需要知道它放在哪）。</summary>
    public string KeyFile { get; set; } = string.Empty;

    /// <summary>密钥文件是否为本次启动新建（首次部署时为 true，应提示妥善保管）。</summary>
    public bool KeyGenerated { get; set; }

    /// <summary>是否存在解密失败的数据（密钥文件被更换 / 丢失）。</summary>
    public bool HasUnavailable =>
        EnrollCodes.Unavailable + RegisterCodes.Unavailable + Webhooks.Unavailable > 0;
}

/// <summary>某一类敏感数据的加密情况。</summary>
public sealed class SecretKindStats
{
    /// <summary>总条数（含空值行）。</summary>
    public int Total { get; set; }

    /// <summary>已加密条数。</summary>
    public int Encrypted { get; set; }

    /// <summary>解密失败条数。</summary>
    public int Unavailable { get; set; }
}
