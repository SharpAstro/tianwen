using System.Collections.Generic;
using System.Text.Json.Serialization;
using TianWen.Hosting.Api;

namespace TianWen.Hosting.Dto;

/// <summary>
/// AOT-safe JSON source generator context for all native-v1 Hosting API DTOs.
/// <para>
/// <b>Public, and deliberately shared.</b> It lives in <c>TianWen.Hosting.Contracts</c> so the server
/// (<c>TianWen.Hosting</c>, via <c>ConfigureHttpJsonOptions</c>) and any client
/// (<c>TianWen.RemoteClient</c>) serialize the same contract through the same generated metadata --
/// one source of truth rather than a hand-copied DTO set that silently drifts. Adding a type here
/// therefore covers both directions at once.
/// </para>
/// <para>
/// The ninaAPI v2 shim keeps its own <c>NinaApiJsonContext</c> in <c>TianWen.Hosting</c> (PascalCase,
/// single-OTA): no client of ours speaks it, so it is not part of the shared contract.
/// </para>
/// </summary>
[JsonSerializable(typeof(ResponseEnvelope<SessionStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<MountStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<GuiderStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaCameraStateDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<OtaInfoDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<string>))]
[JsonSerializable(typeof(ResponseEnvelope<string[]>))]
[JsonSerializable(typeof(ResponseEnvelope<PendingTarget[]>))]
[JsonSerializable(typeof(ResponseEnvelope<ProfileDetailDto>))]
[JsonSerializable(typeof(ResponseEnvelope<Api.ProfileSummaryDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<SessionConfigApiDto>))]
[JsonSerializable(typeof(SessionConfigApiDto))]
[JsonSerializable(typeof(FlatsRequestDto))]
[JsonSerializable(typeof(PendingTarget))]
[JsonSerializable(typeof(Api.CreateProfileRequest))]
[JsonSerializable(typeof(Api.SetProfileRequest))]
[JsonSerializable(typeof(WebSocketEventDto))]
[JsonSerializable(typeof(ResponseEnvelope<WebSocketEventDto>))]
[JsonSerializable(typeof(EnhanceRequestDto))]
[JsonSerializable(typeof(EnhanceStatusDto))]
[JsonSerializable(typeof(ResponseEnvelope<EnhanceStatusDto>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class HostingJsonContext : JsonSerializerContext
{
}
