using IslandHub.Protocol;
using IslandHub.Server.Data;
using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IslandHub.Tests;

/// <summary>
/// 分组（校区）授时偏移的解析规则（#26）。
/// <para>「就近继承 / 层级坏了怎么办 / 越界与 NaN」这三件事都只表现为「时间差了但看不出为什么」，
/// 所以把纯函数钉住，比只测一遍接口返回值可靠得多。</para>
/// </summary>
public sealed class TimeGroupOffsetTests
{
    private static Dictionary<string, double> Offsets(params (string Id, double Seconds)[] items) =>
        items.ToDictionary(i => i.Id, i => i.Seconds, StringComparer.Ordinal);

    private static Dictionary<string, string?> Parents(params (string Id, string? ParentId)[] items) =>
        items.ToDictionary(i => i.Id, i => i.ParentId, StringComparer.Ordinal);

    [Fact]
    public void 没有任何配置时为零()
    {
        Assert.Equal(0, TimeGroupOffset.Resolve("g1", Offsets(), Parents()));
    }

    [Fact]
    public void 设备分组直接配置时取该值()
    {
        var offsets = Offsets(("floor3", 90));
        var parents = Parents(("floor3", "buildingA"), ("buildingA", null));

        Assert.Equal(90, TimeGroupOffset.Resolve("floor3", offsets, parents));
    }

    [Fact]
    public void 分组未配置时继承上级()
    {
        var offsets = Offsets(("buildingA", 180));
        var parents = Parents(("floor3", "buildingA"), ("buildingA", null));

        Assert.Equal(180, TimeGroupOffset.Resolve("floor3", offsets, parents));
    }

    [Fact]
    public void 分组自己配置时优先于上级()
    {
        var offsets = Offsets(("buildingA", 180), ("floor3", -30));
        var parents = Parents(("floor3", "buildingA"), ("buildingA", null));

        Assert.Equal(-30, TimeGroupOffset.Resolve("floor3", offsets, parents));
    }

    [Fact]
    public void 可以沿两级上级上溯()
    {
        // 现在层级只有两层，这里刻意多一层：规则本身不该依赖「层级只有两层」这个当前事实。
        var offsets = Offsets(("campus", 600));
        var parents = Parents(("room", "floor"), ("floor", "campus"), ("campus", null));

        Assert.Equal(600, TimeGroupOffset.Resolve("room", offsets, parents));
    }

    [Fact]
    public void 分组不在层级表里时为零而不是继续猜()
    {
        // 分组已被删除 / 层级数据不完整：宁可退化为全局偏移，也不要挂到一个不存在的上级上。
        var offsets = Offsets(("buildingA", 180));
        var parents = Parents(("buildingA", null));

        Assert.Equal(0, TimeGroupOffset.Resolve("已删除的分组", offsets, parents));
    }

    [Fact]
    public void 层级成环时不会死循环()
    {
        var offsets = Offsets();
        var parents = Parents(("a", "b"), ("b", "a"));

        Assert.Equal(0, TimeGroupOffset.Resolve("a", offsets, parents));
    }

    [Fact]
    public void 自己指向自己时不会死循环()
    {
        Assert.Equal(0, TimeGroupOffset.Resolve("a", Offsets(), Parents(("a", "a"))));
    }

    [Fact]
    public void 环上有一处配置时仍能取到()
    {
        var offsets = Offsets(("b", 45));
        var parents = Parents(("a", "b"), ("b", "a"));

        Assert.Equal(45, TimeGroupOffset.Resolve("a", offsets, parents));
    }

    [Theory]
    [InlineData(100000, TimeGroupOffset.MaxSeconds)]
    [InlineData(-100000, -TimeGroupOffset.MaxSeconds)]
    [InlineData(59.5, 59.5)]
    public void 配置值超范围时被夹取(double raw, double expected)
    {
        Assert.Equal(expected, TimeGroupOffset.Resolve("g", Offsets(("g", raw)), Parents(("g", null))));
    }

    [Fact]
    public void NaN配置按零处理()
    {
        Assert.Equal(0, TimeGroupOffset.Resolve("g", Offsets(("g", double.NaN)), Parents(("g", null))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 设备没有分组时为零(string? groupId)
    {
        Assert.Equal(0, TimeGroupOffset.Resolve(groupId, Offsets(("g", 120)), Parents(("g", null))));
    }

    [Fact]
    public void 分组ID两端的空白会被忽略()
    {
        Assert.Equal(120, TimeGroupOffset.Resolve(" g ", Offsets(("g", 120)), Parents(("g", null))));
    }

    [Fact]
    public void 分组ID大小写不同视为不同分组()
    {
        // 分组 ID 是服务端生成的短 ID，配置键与设备上的值同源；这里钉住「不做大小写归一」，
        // 免得以后有人顺手加一层忽略大小写、把两个不同分组的配置悄悄合并。
        var offsets = Offsets(("GRP", 120));
        var parents = Parents(("GRP", null), ("grp", null));

        Assert.Equal(120, TimeGroupOffset.Resolve("GRP", offsets, parents));
        Assert.Equal(0, TimeGroupOffset.Resolve("grp", offsets, parents));
    }
}

/// <summary>
/// 「分组偏移 → 来源地址 → 真正下发的时间」这条链的服务端行为（#26）。
/// <para>NTP 报文里没有设备身份，A 端只能按来源地址判定校区；判定不出来时**必须**退化为全局偏移，
/// 张冠李戴（把 A 校区的偏移发给 B 校区）比不叠加更糟。</para>
/// </summary>
public sealed class GroupTimeOffsetServiceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-time-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "hub.db");

    private HubStore NewStore() => new(DbPath, new SecretProtector(Path.Combine(dir, "secrets.key")));

    // #87 起 ServerTimeService 收 IOptionsMonitor（要在运行中看到配置变化）；
    // 测试里给个不变的实现即可。
    private static ServerTimeService NewService(HubStore store) =>
        new(new StaticOptionsMonitor<ServerOptions>(new ServerOptions()), new NtpClient(), store,
            NullLogger<ServerTimeService>.Instance);

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

    private static GroupRow NewGroup(string id, string name, string? parentId = null) => new()
    {
        Id = id,
        Name = name,
        ParentId = parentId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task AddDeviceAsync(HubStore store, string id, string ip, string? groupId)
    {
        await store.UpsertDeviceAsync(id, "token-" + id, "教室 " + id, "PC-" + id, "Windows 11",
            "2.1.1.1", "1.0", ip);
        if (groupId is not null)
        {
            await store.SetDeviceGroupAsync(id, groupId);
        }
    }

    /// <summary>取「叠加了分组偏移」与「只叠加全局偏移」两个时间之差（秒）。</summary>
    private static double OffsetOf(ServerTimeService service, string ip) =>
        (service.GetUtcNowForIp(ip) - service.GetUtcNow()).TotalSeconds;

    [Fact]
    public async Task 按来源地址叠加所属分组的偏移()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-a", "东校区"));
        await store.CreateGroupAsync(NewGroup("g-b", "西校区"));
        await AddDeviceAsync(store, "d-a", "10.0.1.10", "g-a");
        await AddDeviceAsync(store, "d-b", "10.0.2.10", "g-b");

        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey,
            HubJson.Serialize(new Dictionary<string, double> { ["g-a"] = 300, ["g-b"] = -120 }));

        var service = NewService(store);
        await service.EnsureAddressOffsetMapAsync(force: true);

        Assert.Equal(300, OffsetOf(service, "10.0.1.10"), 1);
        Assert.Equal(-120, OffsetOf(service, "10.0.2.10"), 1);

        // 没接入的设备（地址不在库里）只叠加全局偏移。
        Assert.Equal(0, OffsetOf(service, "10.0.9.9"), 1);
    }

    [Fact]
    public async Task 分组没配时按上级分组继承()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-campus", "南校区"));
        await store.CreateGroupAsync(NewGroup("g-floor", "南校区 3 楼", "g-campus"));
        await AddDeviceAsync(store, "d-1", "10.1.3.20", "g-floor");

        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey,
            HubJson.Serialize(new Dictionary<string, double> { ["g-campus"] = 240 }));

        var service = NewService(store);
        await service.EnsureAddressOffsetMapAsync(force: true);

        Assert.Equal(240, OffsetOf(service, "10.1.3.20"), 1);
        Assert.Equal(240, service.EffectiveGroupOffsetSeconds("g-floor"), 1);
        Assert.Null(service.GroupOffsetSeconds("g-floor"));
    }

    [Fact]
    public async Task 分组偏移叠加在全局偏移之上()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-a", "东校区"));
        await AddDeviceAsync(store, "d-a", "10.0.1.10", "g-a");
        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey,
            HubJson.Serialize(new Dictionary<string, double> { ["g-a"] = 300 }));

        var service = NewService(store);
        service.SetManualOffsetSeconds(60);
        await service.EnsureAddressOffsetMapAsync(force: true);

        // 全局 60 + 分组 300 = 360。这里必须跟「本机 UTC」比，不能跟 GetUtcNow() 比——
        // 两个都带全局偏移，相减会把它抵消掉，那样即使全局偏移没生效这条断言也照样通过。
        var diff = (service.GetUtcNowForIp("10.0.1.10") - DateTimeOffset.UtcNow).TotalSeconds;
        Assert.Equal(360, diff, 0);
    }

    [Fact]
    public async Task 同一地址上多台设备属于不同偏移的分组时退化为全局偏移()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-a", "东校区"));
        await store.CreateGroupAsync(NewGroup("g-b", "西校区"));
        // 同一 NAT 出口（或本机联调）的多台设备共用一个来源地址：无法判断这台机器属于哪个校区。
        await AddDeviceAsync(store, "d-a", "10.9.9.9", "g-a");
        await AddDeviceAsync(store, "d-b", "10.9.9.9", "g-b");

        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey,
            HubJson.Serialize(new Dictionary<string, double> { ["g-a"] = 300, ["g-b"] = -120 }));

        var service = NewService(store);
        await service.EnsureAddressOffsetMapAsync(force: true);

        Assert.Equal(0, OffsetOf(service, "10.9.9.9"), 1);
    }

    [Fact]
    public async Task 配置损坏时按未配置处理且不影响授时()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-a", "东校区"));
        await AddDeviceAsync(store, "d-a", "10.0.1.10", "g-a");
        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey, "{ 这不是 JSON");

        var service = NewService(store);
        await service.EnsureAddressOffsetMapAsync(force: true);

        Assert.Equal(0, OffsetOf(service, "10.0.1.10"), 1);
    }

    [Fact]
    public async Task 越界的配置值在加载时被夹取()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await store.CreateGroupAsync(NewGroup("g-a", "东校区"));
        await AddDeviceAsync(store, "d-a", "10.0.1.10", "g-a");
        await store.SetSettingAsync(ServerTimeService.GroupOffsetsKey,
            HubJson.Serialize(new Dictionary<string, double> { ["g-a"] = 999_999 }));

        var service = NewService(store);
        await service.EnsureAddressOffsetMapAsync(force: true);

        Assert.Equal(TimeGroupOffset.MaxSeconds, service.GroupOffsetSeconds("g-a"));
        Assert.Equal(TimeGroupOffset.MaxSeconds, OffsetOf(service, "10.0.1.10"), 1);
    }

    [Fact]
    public async Task 心跳会刷新来源地址且空值不会清掉旧值()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await AddDeviceAsync(store, "d-a", "10.0.1.10", null);

        await store.UpdateHeartbeatAsync("d-a", "idle", 0, 0, null, null, "{}", null, "10.0.1.11");
        Assert.Equal("10.0.1.11", (await store.GetDeviceAsync("d-a"))!.IpAddress);

        // 取不到地址时（反向代理没带 X-Forwarded-For 等）必须保留旧值：
        // 地址被清空等于这台设备的校区偏移永久失效，而管理员看不出原因。
        await store.UpdateHeartbeatAsync("d-a", "idle", 0, 0, null, null, "{}", null, null);
        Assert.Equal("10.0.1.11", (await store.GetDeviceAsync("d-a"))!.IpAddress);
    }
}
