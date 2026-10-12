using System.Net.NetworkInformation;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 取本机**主网卡**的物理地址，供管理端做远程唤醒（Wake-on-LAN，#20）。
/// </summary>
/// <remarks>
/// <para>「主网卡」的判定：**已启用、非虚拟、且报文收发正常**的网卡里取第一块。
/// 这条筛选很关键——教室里常见的虚拟网卡（Hyper-V / VMware / VPN / 蓝牙）会让
/// 「随便取一块」取到永远叫不醒机器的那个地址，而管理端显示的又是个合法 MAC，
/// 排查起来毫无线索。</para>
/// <para>取值缓存一次：网卡列表变化不频繁，而心跳每 30 秒发一次，没必要反复枚举。</para>
/// </remarks>
public static class MacAddressProvider
{
    private static readonly Lazy<string?> Cached = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>主网卡 MAC，形如 <c>AA-BB-CC-DD-EE-FF</c>；取不到时返回 <c>null</c>。</summary>
    public static string? Primary() => Cached.Value;

    private static string? Resolve()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              && IsPhysical(nic))
                .Select(nic => nic.GetPhysicalAddress())
                .Where(address => address.GetAddressBytes().Length == 6)
                // 全零地址说明驱动没填（在某些虚拟网卡上会遇到），它发不出有效魔术包。
                .FirstOrDefault(address => address.GetAddressBytes().Any(b => b != 0))
                ?.ToString();
        }
        catch (Exception)
        {
            // 取不到不是致命问题：管理端点「唤醒」时会明确提示「该设备未上报 MAC」。
            // 让心跳因为读网卡失败而中断，才是把小问题放大成大故障。
            return null;
        }
    }

    /// <summary>是不是「真网卡」——虚拟 / 隧道 / 回环都不是。</summary>
    private static bool IsPhysical(NetworkInterface nic)
    {
        var name = $"{nic.Name} {nic.Description}";
        string[] virtualMarkers =
        [
            "virtual", "vmware", "hyper-v", "vethernet", "loopback", "tunnel",
            "tap", "vpn", "蓝牙", "bluetooth", "docker", "wsl",
        ];
        return !virtualMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
