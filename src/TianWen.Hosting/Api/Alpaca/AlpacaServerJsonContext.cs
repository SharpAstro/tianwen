using System.Text.Json;
using System.Text.Json.Serialization;
using TianWen.Lib.Devices.Alpaca;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>
    /// Source-generated metadata for the Alpaca wire envelopes.
    /// <para>
    /// <b>One registration per concrete payload type</b>, because the envelope is generic and Native AOT
    /// cannot resolve <c>AlpacaResponse&lt;object&gt;</c> at runtime -- the same discipline as the
    /// no-<c>ResponseEnvelope&lt;object&gt;</c> rule on the native v1 surface. If a new member returns a
    /// type not listed here, the AOT publish is where it surfaces, not a normal build.
    /// </para>
    /// <para>
    /// The envelope types themselves come from <c>TianWen.Lib.Devices.Alpaca</c> -- the very types the
    /// client deserializes -- so server and client cannot disagree about the shape.
    /// </para>
    /// <para>
    /// <b>Property naming is PascalCase</b> (the DTOs carry explicit <c>[JsonPropertyName]</c>), which is
    /// what ASCOM specifies. This is why the Alpaca surface needs its own context rather than sharing
    /// the camelCase <c>HostingJsonContext</c>.
    /// </para>
    /// </summary>
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict)]
    [JsonSerializable(typeof(AlpacaResponse<bool>))]
    [JsonSerializable(typeof(AlpacaResponse<int>))]
    [JsonSerializable(typeof(AlpacaResponse<double>))]
    [JsonSerializable(typeof(AlpacaResponse<string>))]
    [JsonSerializable(typeof(AlpacaResponse<string[]>))]
    [JsonSerializable(typeof(AlpacaResponse<int[]>))]
    [JsonSerializable(typeof(AlpacaResponse<AlpacaConfiguredDevice[]>))]
    [JsonSerializable(typeof(AlpacaMethodResponse))]
    public partial class AlpacaServerJsonContext : JsonSerializerContext
    {
    }
}
