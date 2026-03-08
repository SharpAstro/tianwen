using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Thin wrapper around <see cref="HttpClient"/> for calling the ASCOM Alpaca REST API.
/// Uses AOT-safe <see cref="AlpacaJsonSerializerContext"/> for all JSON operations.
/// </summary>
internal sealed class AlpacaClient(HttpClient httpClient)
{
    private int _clientTransactionId;

    private int NextTransactionId() => Interlocked.Increment(ref _clientTransactionId);

    private string BuildGetUrl(string baseUrl, string deviceType, int deviceNumber, string endpoint)
    {
        var txId = NextTransactionId();
        return $"{baseUrl}/api/v1/{deviceType}/{deviceNumber}/{endpoint}?ClientID=1&ClientTransactionID={txId.ToString(CultureInfo.InvariantCulture)}";
    }

    private string BuildPutUrl(string baseUrl, string deviceType, int deviceNumber, string endpoint)
    {
        return $"{baseUrl}/api/v1/{deviceType}/{deviceNumber}/{endpoint}";
    }

    private List<KeyValuePair<string, string>> BuildFormFields(IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        var txId = NextTransactionId();
        var formFields = new List<KeyValuePair<string, string>>
        {
            new("ClientID", "1"),
            new("ClientTransactionID", txId.ToString(CultureInfo.InvariantCulture))
        };

        if (parameters is not null)
        {
            formFields.AddRange(parameters);
        }

        return formFields;
    }

    private static async Task<TResponse> DeserializeResponseAsync<TResponse>(HttpResponseMessage response, JsonTypeInfo<TResponse> jsonTypeInfo, CancellationToken cancellationToken)
        where TResponse : class
    {
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken)
            ?? throw new InvalidOperationException("Alpaca response deserialized to null");
    }

    private static void ThrowOnError(int errorNumber, string? errorMessage)
    {
        if (errorNumber != 0)
        {
            throw new AlpacaException(errorNumber, errorMessage);
        }
    }

    /// <summary>
    /// GET a boolean property.
    /// </summary>
    public async Task<bool> GetBoolAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseBoolean, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// GET an integer property.
    /// </summary>
    public async Task<int> GetIntAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseInt32, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// GET a double property.
    /// </summary>
    public async Task<double> GetDoubleAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseDouble, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// GET a string property.
    /// </summary>
    public async Task<string?> GetStringAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseString, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// PUT (invoke a method) on an Alpaca device endpoint with form-encoded parameters.
    /// </summary>
    public async Task PutAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, IEnumerable<KeyValuePair<string, string>>? parameters = null, CancellationToken cancellationToken = default)
    {
        var url = BuildPutUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var content = new FormUrlEncodedContent(BuildFormFields(parameters));
        using var response = await httpClient.PutAsync(url, content, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaMethodResponse, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
    }

    /// <summary>
    /// GET the management API configured devices list.
    /// </summary>
    public async Task<List<AlpacaConfiguredDevice>?> GetConfiguredDevicesAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl}/management/v1/configureddevices?ClientID=1&ClientTransactionID={NextTransactionId().ToString(CultureInfo.InvariantCulture)}";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseListAlpacaConfiguredDevice, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }
}
