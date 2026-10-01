using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.LoadTest;

/// <summary>
/// 集控服务端规模验证工具：模拟任意数量的教室端设备（注册 → 心跳 → 长轮询）。
/// <para>
/// 用途：README 里的规模数字是按实现机制推算的，真实上限取决于你的机器、磁盘与网络。
/// 用这个工具在目标机型上跑一遍，比任何估算都有说服力。
/// </para>
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = HubJson.Create();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = RunOptions.Parse(args);
        if (options is null)
        {
            RunOptions.PrintUsage();
            return 1;
        }

        Console.WriteLine($"目标服务器：{options.ServerUrl}");
        Console.WriteLine($"模拟设备数：{options.DeviceCount}    心跳间隔：{(options.HeartbeatSeconds > 0 ? $"{options.HeartbeatSeconds} 秒" : "跟随服务端建议")}"
                          + $"    长轮询：{(options.Poll ? "开启" : "关闭")}    时长：{(options.DurationSeconds > 0 ? $"{options.DurationSeconds} 秒" : "直到 Ctrl+C")}");
        Console.WriteLine();

        using var handler = new SocketsHttpHandler
        {
            // 长轮询会让每台设备各占一条连接，连接池必须足够大。
            MaxConnectionsPerServer = Math.Max(64, options.DeviceCount + 32),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        var stats = new Stats();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        if (options.DurationSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        }

        // ── 1. 注册虚拟设备 ──
        Console.WriteLine("正在注册虚拟设备（消耗注册码次数）…");
        var devices = new List<VirtualDevice>();
        for (var i = 0; i < options.DeviceCount && !cts.IsCancellationRequested; i++)
        {
            try
            {
                devices.Add(await EnrollAsync(http, options, i, cts.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stats.RecordError("注册", ex);

                // 前几次失败直接打印原因，否则用户只看到「没注册成功」却不知道卡在哪。
                if (stats.EnrollFailed <= 3)
                {
                    Console.WriteLine($"  第 {i + 1} 台注册失败：{ex.Message}");
                }
            }

            if (options.RampMilliseconds > 0)
            {
                await Task.Delay(options.RampMilliseconds, CancellationToken.None);
            }
        }

        if (devices.Count == 0)
        {
            Console.WriteLine("没有成功注册任何设备：请检查服务器地址，以及注册码是否还有可用次数。");
            PrintErrors(stats);
            return 1;
        }

        Console.WriteLine($"注册完成：{devices.Count} 台（失败 {stats.EnrollFailed} 台）");
        Console.WriteLine();

        // ── 2. 心跳 + 长轮询 ──
        var reporter = ReportAsync(stats, devices.Count, cts.Token);
        var loops = devices.Select(d => RunDeviceAsync(http, options, d, stats, cts.Token)).ToArray();
        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径。
        }

        await reporter;
        PrintSummary(stats, devices.Count);

        Console.WriteLine();
        Console.WriteLine("说明：这些虚拟设备会留在服务端设备列表里（名称前缀见 --prefix），");
        Console.WriteLine("      可在管理端「设备管理」中批量删除，或删掉服务端 data 目录后重建。");
        return 0;
    }

    /// <summary>注册一台虚拟设备并返回其凭证。</summary>
    private static async Task<VirtualDevice> EnrollAsync(HttpClient http, RunOptions options, int index,
        CancellationToken cancellationToken)
    {
        var name = $"{options.Prefix}-{index + 1:D4}";
        var request = new EnrollRequest
        {
            EnrollCode = options.EnrollCode,
            // 每台虚拟设备一个稳定唯一指纹，服务端据此分别建档。
            DeviceId = Guid.NewGuid().ToString("N"),
            DeviceName = name,
            MachineName = Environment.MachineName,
            OsVersion = RuntimeInformation.OSDescription,
            ClassIslandVersion = "loadtest",
        };

        var response = await PostAsync<EnrollRequest, EnrollResponse>(http, options, "/client/enroll", null, request,
            cancellationToken);

        return new VirtualDevice
        {
            Name = name,
            DeviceId = response.DeviceId,
            Token = response.DeviceToken,
            HeartbeatSeconds = response.HeartbeatSeconds,
        };
    }

    /// <summary>单台虚拟设备的主循环：心跳 → 长轮询等待变更。</summary>
    private static async Task RunDeviceAsync(HttpClient http, RunOptions options, VirtualDevice device, Stats stats,
        CancellationToken cancellationToken)
    {
        var heartbeatSeconds = options.HeartbeatSeconds > 0
            ? options.HeartbeatSeconds
            : Math.Max(5, device.HeartbeatSeconds);
        var startedAt = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            HeartbeatResponse heartbeat;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                heartbeat = await PostAsync<HeartbeatRequest, HeartbeatResponse>(http, options, "/client/heartbeat",
                    device.Token,
                    new HeartbeatRequest
                    {
                        State = DeviceStates.Idle,
                        AppliedRevision = device.Revision,
                        AppliedPushEpoch = device.PushEpoch,
                        UptimeSeconds = (long)startedAt.Elapsed.TotalSeconds,
                    },
                    cancellationToken);

                stopwatch.Stop();
                stats.RecordHeartbeat(stopwatch.Elapsed);
                device.Revision = heartbeat.Revision;
                device.PushEpoch = heartbeat.PushEpoch;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                stats.RecordError("心跳", ex);
                await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            if (!options.Poll)
            {
                await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, heartbeatSeconds)), cancellationToken);
                continue;
            }

            // 长轮询：挂起等待服务端唤醒；超时（返回无变更）属正常路径。
            var waitStopwatch = Stopwatch.StartNew();
            try
            {
                var wait = await GetAsync<WaitResponse>(http, options,
                    $"/client/wait?revision={device.Revision}&pushEpoch={device.PushEpoch}&timeout={Math.Clamp(heartbeatSeconds, 1, 60)}",
                    device.Token, cancellationToken);

                waitStopwatch.Stop();
                stats.RecordWait(waitStopwatch.Elapsed, wait.Changed);
                device.Revision = wait.Revision;
                device.PushEpoch = wait.PushEpoch;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                stats.RecordError("长轮询", ex);
                await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }

            // 心跳让服务端知道设备还在线；长轮询负责即时性，两者节奏互补。
            await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task ReportAsync(Stats stats, int devices, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var snapshot = stats.Capture();
            Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] 设备 {devices} 台"
                              + $"  心跳 成功 {snapshot.HeartbeatOk} / 失败 {snapshot.HeartbeatFailed}"
                              + $"（平均 {snapshot.HeartbeatAverage:F0} ms，p95 {snapshot.HeartbeatP95:F0} ms）"
                              + $"  长轮询 成功 {snapshot.WaitOk} / 失败 {snapshot.WaitFailed}"
                              + $"（平均 {snapshot.WaitAverage:F0} ms）"
                              + $"  收到变更 {snapshot.Changed} 次");
        }
    }

    private static void PrintSummary(Stats stats, int devices)
    {
        var snapshot = stats.Capture();
        Console.WriteLine();
        Console.WriteLine("══════════ 汇总 ══════════");
        Console.WriteLine($"模拟设备：{devices} 台");
        Console.WriteLine($"心跳：成功 {snapshot.HeartbeatOk}  失败 {snapshot.HeartbeatFailed}"
                          + $"  平均 {snapshot.HeartbeatAverage:F0} ms  p95 {snapshot.HeartbeatP95:F0} ms  最大 {snapshot.HeartbeatMax:F0} ms");
        Console.WriteLine($"长轮询：成功 {snapshot.WaitOk}  失败 {snapshot.WaitFailed}"
                          + $"  平均 {snapshot.WaitAverage:F0} ms  最大 {snapshot.WaitMax:F0} ms");
        Console.WriteLine($"服务端共触发变更 {snapshot.Changed} 次");

        PrintErrors(stats);
    }

    /// <summary>打印错误分类（按出现次数排序）。</summary>
    private static void PrintErrors(Stats stats)
    {
        var errors = stats.Errors();
        if (errors.Count == 0)
        {
            return;
        }

        Console.WriteLine("错误分类：");
        foreach (var (kind, count) in errors)
        {
            Console.WriteLine($"  {kind} × {count}");
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 退出路径。
        }
    }

    // ────────────────────────────── HTTP ──────────────────────────────

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient http, RunOptions options,
        string path, string? deviceToken, TRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(options, path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };
        AddAuth(request, deviceToken);

        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<TResponse> GetAsync<TResponse>(HttpClient http, RunOptions options, string path,
        string? deviceToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(options, path));
        AddAuth(request, deviceToken);

        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<TResponse>(response, cancellationToken);
    }

    private static string Url(RunOptions options, string path) => options.ServerUrl + HubProtocol.ApiPrefix + path;

    private static void AddAuth(HttpRequestMessage request, string? deviceToken)
    {
        if (!string.IsNullOrWhiteSpace(deviceToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"{HubProtocol.DeviceScheme} {deviceToken}");
        }
    }

    private static async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<TResponse>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ApiResult<TResponse>>(text, Json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"服务器返回了无法解析的内容（HTTP {(int)response.StatusCode}）。");
        }

        if (payload is null)
        {
            throw new InvalidOperationException($"服务器返回为空（HTTP {(int)response.StatusCode}）。");
        }

        if (!payload.Ok || payload.Data is null)
        {
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode} {payload.Error?.Code}: {payload.Error?.Message}");
        }

        return payload.Data;
    }
}

/// <summary>一台虚拟设备。</summary>
internal sealed class VirtualDevice
{
    public string Name { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;

    /// <summary>服务端建议的心跳间隔。</summary>
    public int HeartbeatSeconds { get; init; }

    public long Revision { get; set; }
    public long PushEpoch { get; set; }
}

/// <summary>运行参数。</summary>
internal sealed record RunOptions
{
    public string ServerUrl { get; private init; } = "http://127.0.0.1:29800";
    public string EnrollCode { get; private init; } = string.Empty;
    public int DeviceCount { get; private init; } = 50;
    public int HeartbeatSeconds { get; private init; }
    public int RampMilliseconds { get; private init; } = 50;
    public int DurationSeconds { get; private init; }
    public string Prefix { get; private init; } = "loadtest";
    public bool Poll { get; private init; } = true;

    public static RunOptions? Parse(string[] args)
    {
        var result = new RunOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (key)
            {
                case "--server":
                    var server = Next();
                    if (string.IsNullOrWhiteSpace(server))
                    {
                        return null;
                    }

                    result = result with { ServerUrl = server.TrimEnd('/') };
                    break;
                case "--code":
                    result = result with { EnrollCode = Next()?.Trim() ?? string.Empty };
                    break;
                case "--devices":
                    if (!int.TryParse(Next(), out var devices) || devices <= 0)
                    {
                        return null;
                    }

                    result = result with { DeviceCount = devices };
                    break;
                case "--heartbeat":
                    if (!int.TryParse(Next(), out var heartbeat) || heartbeat <= 0)
                    {
                        return null;
                    }

                    result = result with { HeartbeatSeconds = heartbeat };
                    break;
                case "--ramp":
                    if (!int.TryParse(Next(), out var ramp) || ramp < 0)
                    {
                        return null;
                    }

                    result = result with { RampMilliseconds = ramp };
                    break;
                case "--duration":
                    if (!int.TryParse(Next(), out var duration) || duration < 0)
                    {
                        return null;
                    }

                    result = result with { DurationSeconds = duration };
                    break;
                case "--prefix":
                    result = result with { Prefix = Next()?.Trim() ?? "loadtest" };
                    break;
                case "--no-poll":
                    result = result with { Poll = false };
                    break;
                case "--help":
                case "-h":
                    return null;
                default:
                    Console.WriteLine($"未知参数：{key}");
                    return null;
            }
        }

        return string.IsNullOrWhiteSpace(result.ServerUrl) ? null : result;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            集控服务端规模验证工具：模拟任意数量的教室端设备（注册 → 心跳 → 长轮询）。

            用法：
              dotnet run --project src/ControlHub.LoadTest -- --code <注册码> [选项]

            必填：
              --code <注册码>       管理端生成的注册码；模拟 N 台设备会消耗 N 次可用次数，
                                    建议先建一个「不限次数」的注册码。

            选项：
              --server <地址>       服务器地址，默认 http://127.0.0.1:29800
              --devices <数量>      模拟设备数，默认 50
              --heartbeat <秒>      心跳间隔，默认跟随服务端建议（通常 30 秒）
              --ramp <毫秒>         每台设备之间的启动间隔，默认 50（避免瞬时冲击）
              --duration <秒>       运行时长，默认 0 表示一直跑到 Ctrl+C
              --prefix <前缀>       虚拟设备名前缀，默认 loadtest
              --no-poll             只跑心跳，不做长轮询（用于区分两者的负载）
              -h, --help            显示本帮助

            建议：
              · 先用小数量（如 --devices 20 --duration 60）确认链路通，再逐步加量。
              · 观察服务端进程的 CPU / 内存与数据库文件增长，以及本项目输出的 p95 延迟。
              · 结束方式：Ctrl+C（会打印汇总），虚拟设备可在管理端「设备管理」中批量删除。
            """);
    }
}

/// <summary>运行期统计：计数器 + 延迟取样（只保留最近若干次，避免长时间运行吃内存）。</summary>
internal sealed class Stats
{
    private const int MaxSamples = 4000;

    private readonly ConcurrentQueue<double> _heartbeatMs = new();
    private readonly ConcurrentQueue<double> _waitMs = new();
    private readonly ConcurrentDictionary<string, int> _errors = new();
    private long _heartbeatOk;
    private long _heartbeatFailed;
    private long _waitOk;
    private long _waitFailed;
    private long _changed;

    /// <summary>注册失败的设备数。</summary>
    public int EnrollFailed => CountByPrefix("注册：");

    public void RecordHeartbeat(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _heartbeatOk);
        Sample(_heartbeatMs, elapsed.TotalMilliseconds);
    }

    public void RecordWait(TimeSpan elapsed, bool changed)
    {
        Interlocked.Increment(ref _waitOk);
        if (changed)
        {
            Interlocked.Increment(ref _changed);
        }

        Sample(_waitMs, elapsed.TotalMilliseconds);
    }

    public void RecordError(string kind, Exception ex)
    {
        var key = $"{kind}：{Describe(ex)}";
        _errors.AddOrUpdate(key, 1, (_, count) => count + 1);

        switch (kind)
        {
            case "心跳":
                Interlocked.Increment(ref _heartbeatFailed);
                break;
            case "长轮询":
                Interlocked.Increment(ref _waitFailed);
                break;
        }
    }

    public StatsSnapshot Capture()
    {
        var heartbeats = _heartbeatMs.ToArray();
        var waits = _waitMs.ToArray();
        return new StatsSnapshot
        {
            HeartbeatOk = Interlocked.Read(ref _heartbeatOk),
            HeartbeatFailed = Interlocked.Read(ref _heartbeatFailed),
            WaitOk = Interlocked.Read(ref _waitOk),
            WaitFailed = Interlocked.Read(ref _waitFailed),
            Changed = Interlocked.Read(ref _changed),
            HeartbeatAverage = Average(heartbeats),
            HeartbeatP95 = Percentile(heartbeats, 0.95),
            HeartbeatMax = heartbeats.Length == 0 ? 0 : heartbeats.Max(),
            WaitAverage = Average(waits),
            WaitMax = waits.Length == 0 ? 0 : waits.Max(),
        };
    }

    public List<(string Kind, int Count)> Errors() =>
        _errors.OrderByDescending(pair => pair.Value).Select(pair => (pair.Key, pair.Value)).ToList();

    private int CountByPrefix(string prefix) =>
        _errors.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(pair => pair.Value);

    private static void Sample(ConcurrentQueue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > MaxSamples && queue.TryDequeue(out _))
        {
            // 只保留最近若干次取样。
        }
    }

    private static double Average(double[] values) => values.Length == 0 ? 0 : values.Average();

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }

    private static string Describe(Exception ex) => ex switch
    {
        InvalidOperationException => Trim(ex.Message),
        TaskCanceledException => "请求超时",
        HttpRequestException http => $"网络错误：{http.HttpRequestError}",
        _ => ex.GetType().Name,
    };

    private static string Trim(string text) => text.Length > 120 ? text[..120] : text;
}

/// <summary>某一时刻的统计快照。</summary>
internal sealed class StatsSnapshot
{
    public long HeartbeatOk { get; init; }
    public long HeartbeatFailed { get; init; }
    public long WaitOk { get; init; }
    public long WaitFailed { get; init; }
    public long Changed { get; init; }
    public double HeartbeatAverage { get; init; }
    public double HeartbeatP95 { get; init; }
    public double HeartbeatMax { get; init; }
    public double WaitAverage { get; init; }
    public double WaitMax { get; init; }
}
