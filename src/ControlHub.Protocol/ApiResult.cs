namespace ControlHub.Protocol;

/// <summary>
/// 协议错误信息。
/// </summary>
public sealed class ApiError
{
    /// <summary>稳定错误码，见 <see cref="HubErrorCodes"/>。</summary>
    public string Code { get; set; } = HubErrorCodes.Internal;

    /// <summary>面向用户的可读描述。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>补充信息（可选）。</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// 两端统一的响应封套。
/// <para>
/// 无论成功失败，HTTP 响应体都使用该结构：
/// <c>ok</c> 表示是否成功，成功时 <c>data</c> 承载业务数据，失败时 <c>error</c> 承载错误信息。
/// 客户端因此只需要处理一种响应格式。
/// </para>
/// </summary>
/// <typeparam name="T">业务数据类型。</typeparam>
public sealed class ApiResult<T>
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>业务数据（成功时有效）。</summary>
    public T? Data { get; set; }

    /// <summary>错误信息（失败时有效）。</summary>
    public ApiError? Error { get; set; }

    /// <summary>服务器时间（UTC），可用于简单的时间校准。</summary>
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>构造成功响应。</summary>
    public static ApiResult<T> Success(T data) => new()
    {
        Ok = true,
        Data = data,
    };

    /// <summary>构造失败响应。</summary>
    public static ApiResult<T> Failure(string code, string message, string? detail = null) => new()
    {
        Ok = false,
        Error = new ApiError { Code = code, Message = message, Detail = detail },
    };

    /// <summary>构造失败响应。</summary>
    public static ApiResult<T> Failure(ApiError error) => new()
    {
        Ok = false,
        Error = error,
    };
}

/// <summary>
/// 业务异常。服务端端点通过抛出该异常表达失败，由中间件统一转换为
/// <see cref="ApiResult{T}"/> 结构并设置相应 HTTP 状态码。
/// </summary>
public sealed class HubException : Exception
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; }

    /// <summary>补充信息。</summary>
    public string? Detail { get; }

    /// <summary>建议的 HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>创建业务异常。</summary>
    public HubException(string code, string message, int statusCode = 400, string? detail = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Detail = detail;
    }

    /// <summary>参数校验失败。</summary>
    public static HubException Validation(string message) =>
        new(HubErrorCodes.ValidationFailed, message, StatusCodes.BadRequest);

    /// <summary>资源不存在。</summary>
    public static HubException NotFound(string message) =>
        new(HubErrorCodes.NotFound, message, StatusCodes.NotFound);

    /// <summary>资源冲突。</summary>
    public static HubException Conflict(string message) =>
        new(HubErrorCodes.Conflict, message, StatusCodes.Conflict);

    /// <summary>缺少凭证。</summary>
    public static HubException AuthRequired(string? message = null) =>
        new(HubErrorCodes.AuthRequired, message ?? "需要登录。", StatusCodes.Unauthorized);

    /// <summary>凭证无效。</summary>
    public static HubException AuthInvalid(string message) =>
        new(HubErrorCodes.AuthInvalid, message, StatusCodes.Unauthorized);

    /// <summary>HTTP 状态码常量（避免协议层依赖 ASP.NET）。</summary>
    private static class StatusCodes
    {
        public const int BadRequest = 400;
        public const int Unauthorized = 401;
        public const int NotFound = 404;
        public const int Conflict = 409;
    }
}
