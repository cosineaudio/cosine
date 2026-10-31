namespace Cosine.Api.Middleware;

public class SubsonicException : Exception
{
    public Generated.SubsonicErrorCode ErrorCode { get; }

    public SubsonicException(Generated.SubsonicErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
