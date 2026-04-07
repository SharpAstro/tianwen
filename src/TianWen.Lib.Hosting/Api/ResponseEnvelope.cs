namespace TianWen.Lib.Hosting.Api;

/// <summary>
/// Standard response envelope matching the ninaAPI v2 format for compatibility.
/// Every REST and WebSocket response is wrapped in this structure.
/// </summary>
/// <param name="Response">The payload (string, object, array, or null).</param>
/// <param name="Error">Error message, empty string on success.</param>
/// <param name="StatusCode">HTTP status code (200, 400, 404, 500).</param>
/// <param name="Success">True if the request succeeded.</param>
/// <param name="Type">"API" for REST responses, "Socket" for WebSocket push.</param>
public sealed record ResponseEnvelope<T>(
    T? Response,
    string Error,
    int StatusCode,
    bool Success,
    string Type
)
{
    public static ResponseEnvelope<T> Ok(T response, string type = "API")
        => new(response, "", 200, true, type);

    public static ResponseEnvelope<T> Fail(string error, int statusCode = 400, string type = "API")
        => new(default, error, statusCode, false, type);

    public static ResponseEnvelope<T> NotFound(string error, string type = "API")
        => new(default, error, 404, false, type);
}
