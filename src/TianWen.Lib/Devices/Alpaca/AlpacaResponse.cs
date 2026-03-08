using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Standard Alpaca JSON response envelope.
/// </summary>
/// <typeparam name="T">The type of the <see cref="Value"/> payload.</typeparam>
public sealed class AlpacaResponse<T>
{
    [JsonPropertyName("Value")]
    public T? Value { get; set; }

    [JsonPropertyName("ClientTransactionID")]
    public int ClientTransactionID { get; set; }

    [JsonPropertyName("ServerTransactionID")]
    public int ServerTransactionID { get; set; }

    [JsonPropertyName("ErrorNumber")]
    public int ErrorNumber { get; set; }

    [JsonPropertyName("ErrorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Alpaca method response (no return value).
/// </summary>
public sealed class AlpacaMethodResponse
{
    [JsonPropertyName("ClientTransactionID")]
    public int ClientTransactionID { get; set; }

    [JsonPropertyName("ServerTransactionID")]
    public int ServerTransactionID { get; set; }

    [JsonPropertyName("ErrorNumber")]
    public int ErrorNumber { get; set; }

    [JsonPropertyName("ErrorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Alpaca discovery response broadcast via UDP on port 32227.
/// </summary>
public sealed class AlpacaDiscoveryResponse
{
    [JsonPropertyName("AlpacaPort")]
    public int AlpacaPort { get; set; }
}

/// <summary>
/// A single configured device entry from the Alpaca management API.
/// </summary>
public sealed class AlpacaConfiguredDevice
{
    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("DeviceType")]
    public string DeviceType { get; set; } = "";

    [JsonPropertyName("DeviceNumber")]
    public int DeviceNumber { get; set; }

    [JsonPropertyName("UniqueID")]
    public string UniqueID { get; set; } = "";
}
