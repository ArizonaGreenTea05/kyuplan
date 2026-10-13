using System.Net;

namespace Kyuplan.Client.Api;

/// <summary>An API call failed. <see cref="Exception.Message"/> is written for end users and can be shown as is.</summary>
public sealed class PlannerApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Null when the server could not be reached at all.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
