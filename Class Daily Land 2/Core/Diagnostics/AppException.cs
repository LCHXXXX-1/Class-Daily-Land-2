namespace ClassDailyLand2.Core.Diagnostics;

/// <summary>
/// 带错误码的业务异常。抛它就等于"这次失败有明确归类"，
/// 上层只会用 <see cref="Code"/> 查表拿文案，不会去解析消息字符串。
///
/// 没有专门归类的异常走 <see cref="ErrorCodes.Unknown"/>，不需要包装成它。
/// </summary>
public sealed class AppException : Exception
{
    public ErrorCode Code { get; }

    public AppException(ErrorCode code, string? detail = null, Exception? inner = null)
        : base(string.IsNullOrWhiteSpace(detail) ? code.Meaning : detail, inner)
        => Code = code;

    public AppException(ErrorCode code, Exception inner)
        : this(code, null, inner)
    {
    }
}
