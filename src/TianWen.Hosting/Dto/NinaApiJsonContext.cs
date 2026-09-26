using System.Text.Json.Serialization;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto.NinaV2;

namespace TianWen.Hosting.Dto;

/// <summary>
/// AOT-safe JSON context for ninaAPI v2 compatibility endpoints.
/// Uses default (PascalCase) property naming to match ninaAPI v2 conventions.
/// </summary>
// The RUNTIME types of the values in an event payload (a Dictionary<string, object?>). The source generator
// emits metadata only for types a registered root mentions, and an object value of any other type fails at
// SEND time: SCOUT-COMPLETED's int[] never went out on either socket, and the ninaAPI socket could not send
// GUIDE-STEP, FRAME-WRITTEN or NOTIFICATION at all (P0b item 16, #752). BroadcastEventSerializationTests
// sends every event through both contexts.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(DeviceStateDto))]
[JsonSerializable(typeof(ResponseEnvelope<string>))]
[JsonSerializable(typeof(ResponseEnvelope<string[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaCameraInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaMountInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaFocuserInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaFilterWheelInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaGuiderInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaStubInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaWeatherInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaProfileDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaEventDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaImageHistoryDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaGuideStepDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaDeviceListItemDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<Api.ProfileSummaryDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<WebSocketEventDto>))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class NinaApiJsonContext : JsonSerializerContext { }
