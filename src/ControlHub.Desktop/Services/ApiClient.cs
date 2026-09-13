using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;

namespace ControlHub.Desktop.Services;

/// <summary>调用集控服务器（A 端）时发生的错误。</summary>
public sealed class HubApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 面向 A 端服务器的 REST API 客户端（复用 ControlHub.Protocol 的数据模型）。
/// </summary>
public sealed class ApiClient
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, errors) => true,
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>登录并取得令牌。</summary>
    public async Task<string> LoginAsync(string serverUrl, string username, string password)
    {
        var result = await SendAsync<LoginResult>(serverUrl, "/admin/login", HttpMethod.Post, null,
            new { username, password });
        return result.Data?.Token ?? string.Empty;
    }

    /// <summary>GET 请求。</summary>
    public Task<ApiResult<T>> GetAsync<T>(string serverUrl, string token, string path)
        => SendAsync<T>(serverUrl, path, HttpMethod.Get, token, null);

    /// <summary>POST 请求（无返回数据）。</summary>
    public Task<ApiResult<object>> PostAsync(string serverUrl, string token, string path, object? body = null)
        => SendAsync<object>(serverUrl, path, HttpMethod.Post, token, body);

    /// <summary>POST 请求（有返回数据）。</summary>
    public Task<ApiResult<T>> PostAsync<T>(string serverUrl, string token, string path, object? body = null)
        => SendAsync<T>(serverUrl, path, HttpMethod.Post, token, body);

    /// <summary>DELETE 请求。</summary>
    public Task<ApiResult<object>> DeleteAsync(string serverUrl, string token, string path)
        => SendAsync<object>(serverUrl, path, HttpMethod.Delete, token, null);

    private static async Task<ApiResult<T>> SendAsync<T>(string serverUrl, string path, HttpMethod method,
        string? token, object? body)
    {
        var url = serverUrl.TrimEnd('/') + HubProtocol.ApiPrefix + path;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(HubJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (HttpRequestException ex)
        {
            throw new HubApiException("NETWORK", "无法连接服务器：" + ex.Message);
        }

        var text = await response.Content.ReadAsStringAsync();
        var result = HubJson.DeserializeOrDefault(text, new ApiResult<T> { Ok = false });
        if (!result.Ok)
        {
            throw new HubApiException(result.Error?.Code ?? "INTERNAL", result.Error?.Message ?? "服务器返回未知错误。");
        }

        return result;
    }

    private sealed class LoginResult
    {
        public string Token { get; set; } = string.Empty;
    }
}
