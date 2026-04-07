using System.Text.Json.Serialization;
using TianWen.Lib.Hosting.Api;
using TianWen.Lib.Hosting.Dto.NinaV2;

namespace TianWen.Lib.Hosting.Dto;

/// <summary>
/// AOT-safe JSON context for ninaAPI v2 compatibility endpoints.
/// Uses default (PascalCase) property naming to match ninaAPI v2 conventions.
/// </summary>
[JsonSerializable(typeof(ResponseEnvelope<string>))]
[JsonSerializable(typeof(ResponseEnvelope<string[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaCameraInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaMountInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaFocuserInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaFilterWheelInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaGuiderInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaStubInfoDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaProfileDto>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaEventDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<NinaImageHistoryDto[]>))]
[JsonSerializable(typeof(ResponseEnvelope<object>))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class NinaApiJsonContext : JsonSerializerContext { }
