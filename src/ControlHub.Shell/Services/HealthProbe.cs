using System.Net.Http.Json;
using System.Text.Json;

namespace ControlHub.Shell.Services;

/// <summary>健康探针：轮询 A 端的 <c>/api/health</c>，直到就绪或超时。</summary>
internal static class HealthProbe
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>等待服务就绪。</summary>
    /// <param name="baseUrl">形如 <c>http://127.0.0.1:29800</c>。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="aborted">返回 <c>true</c> 表示进程已退出，没必要再等（快速失败，别让用户干等 40 秒）。</param>
    public static async Task<HealthResult> WaitAsync(string baseUrl, TimeSpan timeout,
        CancellationToken cancellationToken = default, Func<bool>? aborted = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (aborted?.Invoke() == true)
            {
                return new HealthResult(false, string.Empty, "进程已退出");
            }

            try
            {
                var response = await Client.GetAsync($"{baseUrl}/api/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    var version = payload.TryGetProperty("data", out var data)
                                  && data.TryGetProperty("version", out var v)
                        ? v.GetString() ?? string.Empty
                        : string.Empty;
                    return new HealthResult(true, version, null);
                }

                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(400, cancellationToken);
        }

        return new HealthResult(false, string.Empty, lastError);
    }
}

/// <summary>健康检查结果。</summary>
/// <param name="Healthy">是否就绪。</param>
/// <param name="Version">A 端版本号。</param>
/// <param name="Error">失败原因（成功时为 <c>null</c>）。</param>
internal sealed record HealthResult(bool Healthy, string Version, string? Error);
