using System.Runtime.InteropServices;

namespace IslandHub.Plugin.Services;

/// <summary>
/// 教室机的**系统主音量**控制（Windows CoreAudio，路线图 #23）。
/// </summary>
/// <remarks>
/// <para>为什么要自己做：ClassIsland 宿主只提供「播放音频」（<c>IAudioService</c>），
/// 没有系统音量控制，这类需求只能由插件落到操作系统上。</para>
/// <para>为什么用 COM 手写而不引第三方包：插件包里每多一个 dll，教室机上的加载面就大一分，
/// 而这里真正需要的只有「读音量 / 设音量 / 切静音」三个动作。</para>
/// <para>⚠️ 下面这些 COM 接口的方法**必须按 vtable 顺序完整声明**（用不到的也要占位）：
/// 少一个或顺序写错，调用会落到别的函数上，表现为莫名其妙的 <c>AccessViolationException</c>，
/// 而且往往只在一部分机器上复现。</para>
/// </remarks>
public static class AudioVolume
{
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int ClsCtxAll = 23;

    /// <summary>读取当前主音量（0~100）与静音状态。</summary>
    public static bool TryGet(out int percent, out bool muted, out string? error)
    {
        percent = 0;
        muted = false;
        try
        {
            var volume = GetEndpointVolume();
            volume.GetMasterVolumeLevelScalar(out var scalar);
            volume.GetMute(out muted);
            // 四舍五入到整数百分比：管理端展示的是 0~100，回读值与原值差 1 以内属正常。
            percent = (int)Math.Round(scalar * 100, MidpointRounding.AwayFromZero);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    /// <summary>设置主音量（0~100，越界夹取）。</summary>
    public static bool TrySetPercent(int percent, out string? error)
    {
        try
        {
            var clamped = Math.Clamp(percent, 0, 100);
            var context = Guid.Empty;
            GetEndpointVolume().SetMasterVolumeLevelScalar(clamped / 100f, ref context);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    /// <summary>切换静音状态。</summary>
    public static bool TrySetMute(bool muted, out string? error)
    {
        try
        {
            var context = Guid.Empty;
            GetEndpointVolume().SetMute(muted, ref context);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    private static IAudioEndpointVolume GetEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        // 取「多媒体」角色的默认输出设备：与用户在音量面板里看到的那一个一致
        // （用 ERole.Console 会在部分机器上取到另一个设备，调了却"没反应"）。
        enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volume);
        return (IAudioEndpointVolume)volume;
    }

    /// <summary>把异常压成一行可上报的原因（COM 异常常常带一大串栈信息）。</summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}：{ex.Message}";

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        void OpenPropertyStore(int access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    /// <remarks>方法顺序即 vtable 顺序，别动（见类注释）。</remarks>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out int count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        void GetVolumeStepInfo(out uint step, out uint stepCount);
        void VolumeStepUp(ref Guid context);
        void VolumeStepDown(ref Guid context);
        void QueryHardwareSupport(out uint mask);
        void GetVolumeRange(out float min, out float max, out float increment);
    }
}

/// <summary>
/// 教室机的**屏幕亮度**控制（Windows 显示器亮度接口，路线图 #24）。
/// </summary>
/// <remarks>
/// <para>用 <c>dxva2.dll</c> 的 <c>Get/SetMonitorBrightness</c>：它就是「屏幕亮度」滑块的底层实现，
/// 对内置屏可用；外接显示器多数需要 DDC/CI 且常常不支持，此时**如实报告不支持**，
/// 而不是假装调成功——教室大屏亮度没变、管理端却显示"已设置"，现场会以为是系统坏了。</para>
/// </remarks>
public static class DisplayBrightness
{
    /// <summary>探测结果：检测到几台物理显示器、其中几台可调、第一台可调显示器当前多少。</summary>
    /// <param name="Total">检测到的物理显示器数量（0 表示系统没报告任何显示器）。</param>
    /// <param name="Adjustable">支持软件调亮度的显示器数量。</param>
    /// <param name="CurrentPercent">第一台可调显示器的当前亮度（0~100）；没有可调显示器时为 -1。</param>
    public readonly record struct ProbeResult(int Total, int Adjustable, int CurrentPercent);

    /// <summary>设置结果：检测到几台、其中几台可调、实际生效几台。</summary>
    /// <param name="Total">检测到的物理显示器数量。</param>
    /// <param name="Adjustable">支持软件调亮度的显示器数量。</param>
    /// <param name="Applied">本次真正改成功的显示器数量。</param>
    public readonly record struct ApplyResult(int Total, int Adjustable, int Applied);

    /// <summary>探测当前显示器亮度（不改动），用于诊断与自动化验证。</summary>
    public static ProbeResult Query()
    {
        var monitors = EnumeratePhysicalMonitors();
        var adjustable = 0;
        var current = -1;
        try
        {
            foreach (var monitor in monitors)
            {
                if (!TryReadPercent(monitor.hPhysicalMonitor, out var percent))
                {
                    continue;
                }

                adjustable++;
                if (current < 0)
                {
                    current = percent;
                }
            }
        }
        finally
        {
            Release(monitors);
        }

        return new ProbeResult(monitors.Length, adjustable, current);
    }

    /// <summary>把所有支持软件调亮度的显示器设成给定百分比（0~100，越界夹取）。</summary>
    public static ApplyResult SetPercent(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var monitors = EnumeratePhysicalMonitors();
        var adjustable = 0;
        var applied = 0;
        try
        {
            foreach (var monitor in monitors)
            {
                if (!GetMonitorBrightness(monitor.hPhysicalMonitor, out var min, out _, out var max) || max <= min)
                {
                    continue;   // 这台显示器不支持软件调亮度（外接屏常见）
                }

                adjustable++;
                var value = (uint)Math.Round(min + (max - min) * (clamped / 100.0), MidpointRounding.AwayFromZero);
                if (SetMonitorBrightness(monitor.hPhysicalMonitor, value))
                {
                    applied++;
                }
            }
        }
        finally
        {
            Release(monitors);
        }

        return new ApplyResult(monitors.Length, adjustable, applied);
    }

    private static bool TryReadPercent(IntPtr handle, out int percent)
    {
        percent = -1;
        if (!GetMonitorBrightness(handle, out var min, out var current, out var max) || max <= min)
        {
            return false;
        }

        percent = (int)Math.Round((current - (double)min) / (max - min) * 100, MidpointRounding.AwayFromZero);
        return true;
    }

    private static void Release(PHYSICAL_MONITOR[] monitors)
    {
        if (monitors.Length > 0)
        {
            DestroyPhysicalMonitors((uint)monitors.Length, monitors);
        }
    }

    private static PHYSICAL_MONITOR[] EnumeratePhysicalMonitors()
    {
        var handles = new List<IntPtr>();
        // 回调是同步调用期间有效的局部委托：EnumDisplayMonitors 返回后就不再使用。
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                handles.Add(hMonitor);
                return true;
            }, IntPtr.Zero);

        var result = new List<PHYSICAL_MONITOR>();
        foreach (var handle in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count == 0)
            {
                continue;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (GetPhysicalMonitorsFromHMONITOR(handle, count, monitors))
            {
                result.AddRange(monitors);
            }
        }

        return result.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count,
        [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint brightness);
}
