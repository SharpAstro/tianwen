using System.Collections.Generic;
using System.Text.Json.Serialization;
using TianWen.Hosting.Api;

namespace TianWen.Hosting.Dto;

/// <summary>
/// AOT-safe JSON source generator context for all Hosting API DTOs.
/// </summary>
[JsonSerializable(typeof(ResponseEnvelope<SessionStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<MountStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<GuiderStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaCameraStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaInfoDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<string>))]
[JsonSerializable(typeof(ResponseEnvelope<string[]>))]
[JsonSerializable(typeof(ResponseEnvelope<object>))]
[JsonSerializable(typeof(ResponseEnvelope<ProfileDetailDto>))]
[JsonSerializable(typeof(ResponseEnvelope<Api.ProfileSummaryDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<SessionConfigApiDto>))]
[JsonSerializable(typeof(SessionConfigApiDto))]
[JsonSerializable(typeof(PendingTarget))]
[JsonSerializable(typeof(Api.CreateProfileRequest))]
[JsonSerializable(typeof(Api.SetProfileRequest))]
[JsonSerializable(typeof(WebSocketEventDto))]
[JsonSerializable(typeof(ResponseEnvelope<WebSocketEventDto>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class HostingJsonContext : JsonSerializerContext
{
}
