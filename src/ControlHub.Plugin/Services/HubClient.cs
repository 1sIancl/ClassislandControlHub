using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Plugin.Services;

/// <summary>客户端调用集控服务器时发生的协议级错误。</summary>
public sealed class HubApiException : Exception
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    public HubApiException(string code, string message, int statusCode = 0)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

/// <summary>
/// 与集控服务器（A 端）通信的 HTTP 客户端。
/// <para>
/// 覆盖设备注册、配置查询、心跳、拉取配置、长轮询、结果上报与日志上传等全部客户端接口。
/// 所有方法都是无状态的（显式传入服务器地址与令牌），便于同步引擎自由调度。
/// </para>
/// </summary>
public sealed class HubClient
{
    /// <summary>是否允许连接不受信任的 HTTPS 服务器（自签名证书）。</summary>
    private static volatile bool _allowInsecureTls;

    /// <summary>共享的 HTTP 客户端。长轮询可能持续 60 秒以上，故不设置过短的总超时。</summary>
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    _allowInsecureTls || errors == SslPolicyErrors.None,
            },
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90),
            DefaultRequestVersion = new Version(1, 1),
        };
    }

    /// <summary>同步「允许不安全证书」开关到共享处理器。</summary>
    public void SetAllowInsecureTls(bool allow) => _allowInsecureTls = allow;

    /// <summary>查询服务器公开配置（注册前即可调用）。</summary>
    public async Task<ServerInfoDto> GetServerInfoAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<ServerInfoDto>(baseUrl, "/server/info", HttpMethod.Get,
            auth: null, body: null, timeout: TimeSpan.FromSeconds(15), cancellationToken);
        return result.Data!;
    }

    /// <summary>
    /// 查询服务器 UTC 时间（用于本机时钟同步）。返回响应封套携带的服务器时间。
    /// </summary>
    public async Task<DateTimeOffset> GetServerTimeAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<TimeSyncResponse>(baseUrl, "/client/time", HttpMethod.Get,
            auth: null, body: null, timeout: TimeSpan.FromSeconds(10), cancellationToken);
        return result.Data?.ServerTime ?? result.ServerTime;
    }

    /// <summary>注册设备。</summary>
    public async Task<EnrollResponse> EnrollAsync(string baseUrl, EnrollRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<EnrollResponse>(baseUrl, "/client/enroll", HttpMethod.Post,
            auth: null, body: request, timeout: TimeSpan.FromSeconds(20), cancellationToken);
        return result.Data!;
    }

    /// <summary>上报心跳。</summary>
    public async Task<HeartbeatResponse> HeartbeatAsync(string baseUrl, string deviceToken,
        HeartbeatRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<HeartbeatResponse>(baseUrl, "/client/heartbeat", HttpMethod.Post,
            auth: HubProtocol.DeviceScheme + " " + deviceToken, body: request,
            timeout: TimeSpan.FromSeconds(20), cancellationToken);
        return result.Data!;
    }

    /// <summary>拉取配置。版本一致且非强制时返回 <c>null</c> 表示无需下发。</summary>
    public async Task<SyncResponse?> SyncAsync(string baseUrl, string deviceToken, long revision,
        long pushEpoch, IReadOnlyCollection<string> sections, bool force,
        CancellationToken cancellationToken = default)
    {
        var query = $"/client/sync?revision={revision}&pushEpoch={pushEpoch}"
                    + (force ? "&force=true" : string.Empty);
        if (sections.Count > 0)
        {
            query += "&sections=" + Uri.EscapeDataString(string.Join(",", sections));
        }

        var result = await SendAsync<SyncResponse?>(baseUrl, query, HttpMethod.Get,
            auth: HubProtocol.DeviceScheme + " " + deviceToken, body: null,
            timeout: TimeSpan.FromSeconds(30), cancellationToken);
        return result.Data;
    }

    /// <summary>长轮询等待变更。</summary>
    public async Task<WaitResponse> WaitAsync(string baseUrl, string deviceToken, long revision,
        long pushEpoch, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var query = $"/client/wait?revision={revision}&pushEpoch={pushEpoch}&timeout={timeoutSeconds}";
        var result = await SendAsync<WaitResponse>(baseUrl, query, HttpMethod.Get,
            auth: HubProtocol.DeviceScheme + " " + deviceToken, body: null,
            timeout: TimeSpan.FromSeconds(timeoutSeconds + 10), cancellationToken);
        return result.Data!;
    }

    /// <summary>上报配置应用结果。</summary>
    public async Task ReportAsync(string baseUrl, string deviceToken, ApplyReportRequest request,
        CancellationToken cancellationToken = default)
    {
        await SendAsync<bool>(baseUrl, "/client/report", HttpMethod.Post,
            auth: HubProtocol.DeviceScheme + " " + deviceToken, body: request,
            timeout: TimeSpan.FromSeconds(20), cancellationToken);
    }

    /// <summary>上传本地日志。</summary>
    public async Task UploadLogsAsync(string baseUrl, string deviceToken, List<LogEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await SendAsync<bool>(baseUrl, "/client/logs", HttpMethod.Post,
            auth: HubProtocol.DeviceScheme + " " + deviceToken,
            body: new LogReportRequest { Entries = entries },
            timeout: TimeSpan.FromSeconds(20), cancellationToken);
    }

    private async Task<ApiResult<T>> SendAsync<T>(string baseUrl, string path, HttpMethod method,
        string? auth, object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var url = baseUrl.TrimEnd('/') + HubProtocol.ApiPrefix + path;

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (auth is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        if (body is not null)
        {
            request.Content = new StringContent(HubJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HubApiException("TIMEOUT", $"请求服务器超时：{method} {path}");
        }
        catch (HttpRequestException ex)
        {
            throw new HubApiException("NETWORK", $"无法连接服务器：{ex.Message}");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = HubJson.DeserializeOrDefault(text, new ApiResult<T> { Ok = false });

        // 服务器返回的失败封套。
        if (!result.Ok)
        {
            throw new HubApiException(
                result.Error?.Code ?? HubErrorCodes.Internal,
                result.Error?.Message ?? "服务器返回了未知错误。",
                (int)response.StatusCode);
        }

        return result;
    }
}
