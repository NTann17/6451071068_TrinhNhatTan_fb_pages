using System.Net;

namespace webhook_service.Services;

public sealed class FacebookApiException : Exception
{
    public FacebookApiException(
        HttpStatusCode statusCode,
        string friendlyMessage,
        string? responseBody,
        string endpoint,
        string? errorCode = null)
        : base(friendlyMessage)
    {
        StatusCode = statusCode;
        FriendlyMessage = friendlyMessage;
        ResponseBody = responseBody;
        Endpoint = endpoint;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    public string FriendlyMessage { get; }

    public string? ResponseBody { get; }

    public string Endpoint { get; }

    public string? ErrorCode { get; }
}