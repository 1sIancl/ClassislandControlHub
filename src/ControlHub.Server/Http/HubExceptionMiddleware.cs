using ControlHub.Protocol;

namespace ControlHub.Server.Http;

/// <summary>
/// 统一异常处理中间件。
/// <para>
/// 业务层通过抛出 <see cref="HubException"/> 表达失败，这里把它转换成
/// 协议规定的 <see cref="ApiResult{T}"/> 结构，使客户端始终只需处理一种响应格式。
/// </para>
/// </summary>
public sealed class HubExceptionMiddleware(RequestDelegate next, ILogger<HubExceptionMiddleware> logger)
{
    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (HubException ex)
        {
            // 业务异常属于预期内的失败路径，按 Warning 级别记录即可。
            logger.LogWarning("业务异常：{Code} {Message}（{Path}）", ex.Code, ex.Message, context.Request.Path);
            await WriteAsync(context, ex.StatusCode, ApiResult<object>.Failure(
                new ApiError { Code = ex.Code, Message = ex.Message, Detail = ex.Detail }));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端主动断开（例如长轮询超时后关闭连接），无需当作错误。
            logger.LogDebug("请求被客户端取消：{Path}", context.Request.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理请求 {Method} {Path} 时发生未处理异常。",
                context.Request.Method, context.Request.Path);

            // 不向客户端泄露堆栈细节。
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                ApiResult<object>.Failure(HubErrorCodes.Internal, "服务器内部错误，请联系管理员查看服务端日志。"));
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, ApiResult<object> result)
    {
        if (context.Response.HasStarted)
        {
            // 响应已经开始写出，无法再改写状态码与正文。
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(HubJson.Serialize(result));
    }
}
