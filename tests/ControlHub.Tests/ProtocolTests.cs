using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Tests;

/// <summary>协议层的基础约束：这些一旦破了，两端就会出现「能连上但行为不对」的诡异问题。</summary>
public sealed class ProtocolTests
{
    [Fact]
    public void 内容包校验和对相同内容稳定_对变化敏感()
    {
        var a = new ContentBundleDto
        {
            Subjects = [new SubjectDto { Id = "s-1", Name = "语文" }],
        };
        var b = new ContentBundleDto
        {
            Subjects = [new SubjectDto { Id = "s-1", Name = "语文" }],
        };
        var changed = new ContentBundleDto
        {
            Subjects = [new SubjectDto { Id = "s-1", Name = "数学" }],
        };

        Assert.Equal(HubChecksum.ComputeOf(a), HubChecksum.ComputeOf(b));
        Assert.NotEqual(HubChecksum.ComputeOf(a), HubChecksum.ComputeOf(changed));
    }

    [Fact]
    public void 设备令牌是URL安全的随机串且互不相同()
    {
        var first = HubChecksum.NewToken(48);
        var second = HubChecksum.NewToken(48);

        // 令牌要放进 Authorization 头与 URL 参数，因此必须是 URL 安全字符（base64url）。
        Assert.Matches(@"^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, second);

        // base64 编码 48 字节 → 64 个字符（去掉补位），这里只约束「足够长」，不写死编码细节。
        Assert.True(first.Length >= 48, $"令牌长度 {first.Length} 偏短");
    }

    [Fact]
    public void 权限集合会去重并保留已知键()
    {
        var normalized = PermissionKeys.Normalize(["devices.read", "devices.read", "remote.write"]);

        Assert.Contains("devices.read", normalized);
        Assert.Contains("remote.write", normalized);
        Assert.Equal(normalized.Count, normalized.Distinct().Count());
    }

    [Fact]
    public void 版本号与接口前缀格式符合约定()
    {
        // A 端版本号形如 1.3.2（三段数字）——发布脚本与兼容性表都按这个格式表述。
        Assert.Matches(@"^\d+\.\d+\.\d+$", HubProtocol.ProductVersion);
        Assert.StartsWith("/", HubProtocol.ApiPrefix);
        Assert.Equal("HubDevice", HubProtocol.DeviceScheme);
    }

    [Fact]
    public void 长轮询与心跳默认值在合理区间()
    {
        // 这两个值是要载重的：心跳太快会压垮服务端（数百台设备每秒都在写库），
        // 太慢则「掉线」判定迟钝；长轮询间隔太长会让配置下发延迟变大。改错很难在测试之外被发现。
        //
        // 注意：长轮询间隔（25 秒）**故意小于**心跳（30 秒）——它只是「挂起等变更」，
        // 到点没变更就重新挂一次，比心跳快一点才能更快拿到推送。别把它改成「必须大于心跳」。
        Assert.InRange(HubProtocol.DefaultHeartbeatSeconds, 5, 120);
        Assert.InRange(HubProtocol.DefaultLongPollSeconds, 5, 60);
    }
}
