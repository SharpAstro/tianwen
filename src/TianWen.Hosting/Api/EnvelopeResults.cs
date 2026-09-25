using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace TianWen.Hosting.Api;

/// <summary>
/// A native v1 answer: the envelope as JSON, under the HTTP status the envelope carries.
/// </summary>
/// <remarks>
/// Every endpoint used to answer <c>Results.Json(envelope, typeInfo)</c>, which is HTTP 200 whatever the
/// envelope says, so a client reading the status alone could not tell an error from an answer. The preview
/// client reads nothing else before decoding a JPEG, and took a JSON "no frame yet" for a picture on every
/// poll (P0b item 6 of docs/plans/hardware-in-the-server.md, #752). The JSON client already trusts the
/// envelope over the status, so it is unaffected. The ninaAPI shim keeps its own convention, since Touch N
/// Stars reads it, and the Alpaca plane keeps ASCOM's (HTTP 200 with an error number).
/// </remarks>
internal static class EnvelopeResults
{
    public static IResult Json<T>(ResponseEnvelope<T> envelope, JsonTypeInfo<ResponseEnvelope<T>> typeInfo)
        => Results.Json(envelope, typeInfo, statusCode: envelope.StatusCode);
}
