using ControlHub.Server.Data;
using ControlHub.Server.Services;
using Microsoft.Data.Sqlite;

namespace ControlHub.Tests;

/// <summary>
/// 静态敏感数据加密（#36，见 ADR 0005）在数据层的端到端行为：
/// 落库是否为密文、能否按指纹查找、老明文行是否兼容、启动迁移是否幂等、密钥丢失后是否 fail-closed。
/// <para>这些用例全部直接读 SQLite 原始值断言——只看 API 返回值是不够的：
/// 「接口返回成功但库底还是明文」正是要防的静默失败。</para>
/// </summary>
public sealed class SecretStorageTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-store-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "hub.db");

    private HubStore NewStore(string keyFile = "secrets.key") =>
        new(DbPath, new SecretProtector(Path.Combine(dir, keyFile)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    /// <summary>直接读库（绕过 HubStore），返回指定两列的原始字符串。</summary>
    private async Task<List<(string First, string? Second)>> QueryRawAsync(string sql)
    {
        var result = new List<(string, string?)>();
        await using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return result;
    }

    private async Task ExecuteRawAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static EnrollCodeRow NewEnrollCode(string code, int maxUses = 1) => new()
    {
        Code = code,
        Note = "测试",
        MaxUses = maxUses,
        UsedCount = 0,
        ExpiresAt = null,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task 新建注册码以密文落库并写入指纹()
    {
        var store = NewStore();
        await store.InitializeAsync();

        var created = NewEnrollCode("ABCD1234");
        await store.CreateEnrollCodeAsync(created);

        var rows = await QueryRawAsync("SELECT code, code_hash FROM enroll_codes;");
        var row = Assert.Single(rows);
        Assert.StartsWith("enc:v1:", row.First, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.Second));
        Assert.DoesNotContain("ABCD1234", row.First, StringComparison.Ordinal);

        // 创建后要把指纹回填到实体上：端点的创建响应靠它给出指纹引用（否则界面上是「(无)」）。
        Assert.Equal(row.Second, created.CodeHash);
    }

    [Fact]
    public async Task 迁移会清洗历史审计记录里的明文注册码()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // 场景一：老版本把「仍在表里」的注册码明文写进了审计 target（升级时明文还在手上，可精确替换成引用）。
        await ExecuteRawAsync(
            "INSERT INTO enroll_codes (code, note, max_uses, used_count, expires_at, enabled, created_at) "
            + "VALUES ('LEGACY02', '', 1, 0, NULL, 1, '2026-01-01T00:00:00.0000000+00:00');");
        await ExecuteRawAsync(
            "INSERT INTO audit_logs (ts, actor, action, target, detail, ip) "
            + "VALUES ('2026-01-01T00:00:00.0000000+00:00', 'admin', 'enrollcode.create', 'LEGACY02', "
            + "'生成注册码 LEGACY02，可用次数 1。', NULL);");

        // 场景二：老版本把「已删除」的注册码明文留在审计里——表里已经没这行，明文不可得，只能替换成占位符。
        await ExecuteRawAsync(
            "INSERT INTO audit_logs (ts, actor, action, target, detail, ip) "
            + "VALUES ('2026-01-01T00:00:00.0000000+00:00', 'admin', 'enrollcode.delete', 'GONE9999', "
            + "'删除了注册码。', NULL);");

        // 计数是「受影响行次」（target 与 detail 两处各算一次），只用于日志，断言命中即可。
        var result = await store.MigrateSecretsAsync();
        Assert.True(result.AuditReferencesScrubbed >= 2);

        var audit = await QueryRawAsync("SELECT target, detail FROM audit_logs ORDER BY id;");
        Assert.Equal(2, audit.Count);
        Assert.StartsWith(HubStore.SecretReferencePrefix, audit[0].First, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY02", audit[0].Second, StringComparison.Ordinal);
        Assert.DoesNotContain("GONE9999", audit[1].First, StringComparison.Ordinal);

        // 幂等：再跑一次不会重复改动，也不会报错。
        var again = await store.MigrateSecretsAsync();
        Assert.False(again.Changed);
    }

    [Fact]
    public async Task 注册码按指纹查找且忽略大小写与空格()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateEnrollCodeAsync(NewEnrollCode("ABCD1234"));

        // 用户输入带空格、小写，都应查到同一个码。
        var found = await store.GetEnrollCodeAsync(" abcd1234 ");

        Assert.NotNull(found);
        Assert.Equal("ABCD1234", found.Code);
        Assert.False(found.CodeUnavailable);

        Assert.True(await store.ConsumeEnrollCodeAsync("abcd1234"));
        Assert.False(await store.ConsumeEnrollCodeAsync("ABCD1234")); // 次数已用完
    }

    [Fact]
    public async Task 老库明文注册码仍可用且迁移后升级为密文()
    {
        var store = NewStore();
        await store.InitializeAsync();

        // 模拟升级前写入的老数据：明文 code、没有指纹。
        await ExecuteRawAsync(
            "INSERT INTO enroll_codes (code, note, max_uses, used_count, expires_at, enabled, created_at) "
            + "VALUES ('LEGACY01', '老数据', 2, 0, NULL, 1, '2026-01-01T00:00:00.0000000+00:00');");

        // 迁移前：靠「指纹为空则按明文匹配」的兼容路径仍能查到并使用。
        var before = await store.GetEnrollCodeAsync("legacy01");
        Assert.NotNull(before);
        Assert.True(await store.ConsumeEnrollCodeAsync("LEGACY01"));

        // 启动迁移：补指纹 + 加密。
        var result = await store.MigrateSecretsAsync();
        Assert.Equal(1, result.EnrollCodesEncrypted);
        Assert.Equal(1, result.EnrollCodesFingerprinted);

        var rows = await QueryRawAsync("SELECT code, code_hash FROM enroll_codes;");
        var row = Assert.Single(rows);
        Assert.StartsWith("enc:v1:", row.First, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.Second));

        // 迁移后仍能查到（指纹路径），且已用次数保留。
        var after = await store.GetEnrollCodeAsync("LEGACY01");
        Assert.NotNull(after);
        Assert.Equal("LEGACY01", after.Code);
        Assert.Equal(1, after.UsedCount);

        // 迁移幂等：重复执行不再改动。
        var again = await store.MigrateSecretsAsync();
        Assert.False(again.Changed);
        Assert.Equal(0, again.Unrecoverable);
    }

    [Fact]
    public async Task 密钥丢失后注册码不可用但不静默放行()
    {
        var first = NewStore("key-a.key");
        await first.InitializeAsync();
        await first.CreateEnrollCodeAsync(NewEnrollCode("ABCD1234"));

        // 换一把密钥（模拟 secrets.key 被更换 / 丢失）。
        var second = NewStore("key-b.key");
        await second.InitializeAsync();

        // 指纹用新密钥算，匹配不到旧行 → 表现为「注册码不存在」（教室端注册会被拒绝）。
        Assert.Null(await second.GetEnrollCodeAsync("ABCD1234"));

        // 但管理端列表能读出该行，并明确标记为不可用（不是空值、不是明文）。
        var listed = Assert.Single(await second.GetEnrollCodesAsync());
        Assert.True(listed.CodeUnavailable);
        Assert.Equal(string.Empty, listed.Code);
        Assert.StartsWith(HubStore.SecretReferencePrefix, HubStore.SecretReference(listed.CodeHash),
            StringComparison.Ordinal);

        // 诊断计数能看到「1 条无法解密」。
        var stats = await second.GetSecretEncryptionStatsAsync();
        Assert.Equal(1, stats.EnrollCodes.Total);
        Assert.Equal(1, stats.EnrollCodes.Encrypted);
        Assert.Equal(1, stats.EnrollCodes.Unavailable);
        Assert.True(stats.HasUnavailable);

        // 明文不可得时，仍可用指纹引用删除该行。
        var reference = HubStore.SecretReference(listed.CodeHash);
        await second.DeleteEnrollCodeAsync(reference);
        Assert.Empty(await second.GetEnrollCodesAsync());
    }

    [Fact]
    public async Task 指纹引用可定位到行且与明文是两套输入()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateEnrollCodeAsync(NewEnrollCode("ABCD1234"));
        var reference = store.ComputeCodeReference("ABCD1234");

        // 引用来自库中的 code_hash 前 8 位；它能让管理端在明文不可用时操作该行。
        // 注意：拒绝把引用当注册码提交的责任在教室端 / 自助注册端点（那里有显式校验），
        // HubStore 这一层刻意支持引用（管理端删除 / 编辑要用）。
        var byReference = await store.GetEnrollCodeAsync(reference);
        Assert.NotNull(byReference);
        Assert.Equal("ABCD1234", byReference.Code);
    }

    [Fact]
    public async Task Webhook密钥加密落库且可读回明文()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.CreateWebhookAsync(new WebhookRow
        {
            Id = "hook-1",
            Name = "教师群",
            Url = "https://example.com/hook",
            Kind = "dingtalk",
            Secret = "SEC-123456",
            Events = "[\"deviceOffline\"]",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var rows = await QueryRawAsync("SELECT secret, url FROM webhooks;");
        var row = Assert.Single(rows);
        Assert.StartsWith("enc:v1:", row.First, StringComparison.Ordinal);

        var hook = await store.GetWebhookAsync("hook-1");
        Assert.NotNull(hook);
        Assert.Equal("SEC-123456", hook.Secret);
        Assert.False(hook.SecretUnavailable);
    }

    [Fact]
    public async Task 密钥丢失后Webhook保留原密文且不静默按未配置处理()
    {
        var first = NewStore("key-a.key");
        await first.InitializeAsync();
        await first.CreateWebhookAsync(new WebhookRow
        {
            Id = "hook-1",
            Name = "教师群",
            Url = "https://example.com/hook",
            Kind = "dingtalk",
            Secret = "SEC-123456",
            Events = "[\"deviceOffline\"]",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var second = NewStore("key-b.key");
        await second.InitializeAsync();

        var hook = await second.GetWebhookAsync("hook-1");
        Assert.NotNull(hook);
        Assert.True(hook.SecretUnavailable);
        Assert.Equal(string.Empty, hook.Secret);

        // 只改名字、不填新密钥：必须保留原密文（否则换回密钥也救不回来）。
        hook.Name = "改名后";
        Assert.True(await second.UpdateWebhookAsync(hook));

        var rows = await QueryRawAsync("SELECT secret, name FROM webhooks;");
        var row = Assert.Single(rows);
        Assert.StartsWith("enc:v1:", row.First, StringComparison.Ordinal);
        Assert.Equal("改名后", row.Second);

        // 原密钥的 store 仍能解出明文（证明密文没被覆盖）。
        var restored = await first.GetWebhookAsync("hook-1");
        Assert.NotNull(restored);
        Assert.Equal("SEC-123456", restored.Secret);
    }

    [Fact]
    public async Task 邀请码同样以密文落库并可凭明文消费()
    {
        var store = NewStore();
        await store.InitializeAsync();

        await store.CreateRegisterCodeAsync(new RegisterCodeRow
        {
            Code = "INVITE01",
            Note = "给张老师",
            Permissions = [],
            MaxUses = 1,
            UsedCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var rows = await QueryRawAsync("SELECT code, code_hash FROM register_codes;");
        var row = Assert.Single(rows);
        Assert.StartsWith("enc:v1:", row.First, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(row.Second));

        // 明文（大写 / 小写都行）可查到并消费一次。
        var found = await store.GetRegisterCodeAsync("invite01");
        Assert.NotNull(found);
        Assert.Equal("INVITE01", found.Code);

        var (consumed, error) = await store.ConsumeRegisterCodeAsync("INVITE01");
        Assert.Null(error);
        Assert.NotNull(consumed);
        Assert.Equal(1, consumed.UsedCount);
    }

    [Fact]
    public async Task 迁移不会二次加密已完成的库()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateEnrollCodeAsync(NewEnrollCode("ABCD1234"));
        await store.CreateRegisterCodeAsync(new RegisterCodeRow
        {
            Code = "INVITE01",
            MaxUses = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await store.CreateWebhookAsync(new WebhookRow
        {
            Id = "hook-1",
            Name = "群",
            Url = "https://example.com/hook",
            Secret = "SEC",
            Events = "[\"deviceOffline\"]",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var first = await store.MigrateSecretsAsync();
        var second = await store.MigrateSecretsAsync();

        Assert.False(first.Changed);
        Assert.False(second.Changed);
        Assert.Empty(second.Errors);
        Assert.Equal(0, second.Unrecoverable);

        // 加密后的值仍可正常解出。
        Assert.Equal("ABCD1234", (await store.GetEnrollCodeAsync("ABCD1234"))!.Code);
        Assert.Equal("SEC", (await store.GetWebhookAsync("hook-1"))!.Secret);
    }
}
