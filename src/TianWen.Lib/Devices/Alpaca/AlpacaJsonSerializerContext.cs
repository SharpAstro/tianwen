using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// AOT-safe JSON source generator context for Alpaca response types.
/// </summary>
[JsonSerializable(typeof(AlpacaResponse<bool>))]
[JsonSerializable(typeof(AlpacaResponse<int>))]
[JsonSerializable(typeof(AlpacaResponse<double>))]
[JsonSerializable(typeof(AlpacaResponse<string>))]
[JsonSerializable(typeof(AlpacaResponse<int[]>))]
[JsonSerializable(typeof(AlpacaResponse<string[]>))]
[JsonSerializable(typeof(AlpacaResponse<double[]>))]
[JsonSerializable(typeof(AlpacaResponse<List<AlpacaConfiguredDevice>>))]
[JsonSerializable(typeof(AlpacaMethodResponse))]
[JsonSerializable(typeof(AlpacaDiscoveryResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AlpacaJsonSerializerContext : JsonSerializerContext
{
}
