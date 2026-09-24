using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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
        // An endpoint may carry its own query (canmoveaxis?Axis=0, axisrates?Axis=1, destinationsideofpier?...),
        // and a second '?' would glue the client fields onto its last value: Axis=0?ClientID=1.
        var separator = endpoint.Contains('?') ? '&' : '?';
        return $"{baseUrl}/api/v1/{deviceType}/{deviceNumber}/{endpoint}{separator}ClientID=1&ClientTransactionID={txId.ToString(CultureInfo.InvariantCulture)}";
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
    /// GET a string array property (e.g. filterwheel/filternames).
    /// </summary>
    public async Task<string[]?> GetStringArrayAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseStringArray, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// GET telescope/axisrates: an array of <c>{ Minimum, Maximum }</c> in degrees per second.
    /// </summary>
    public async Task<AlpacaAxisRate[]?> GetAxisRatesAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseAlpacaAxisRateArray, cancellationToken);
        ThrowOnError(result.ErrorNumber, result.ErrorMessage);
        return result.Value;
    }

    /// <summary>
    /// GET an integer array property (e.g. filterwheel/focusoffsets).
    /// </summary>
    public async Task<int[]?> GetIntArrayAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        var result = await DeserializeResponseAsync(response, AlpacaJsonSerializerContext.Default.AlpacaResponseInt32Array, cancellationToken);
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
    /// GET an image array endpoint using the binary ImageBytes transfer. Offers
    /// <c>application/imagebytes</c> first (then <c>application/json</c>) via the
    /// <c>Accept</c> header and returns the raw ImageBytes payload when the server honours it.
    /// Throws <see cref="NotSupportedException"/> if the server responds with JSON instead;
    /// the legacy (slow) JSON ImageArray decode is intentionally not implemented, since
    /// effectively all current Alpaca camera servers support ImageBytes. Decode the returned
    /// payload via <see cref="AlpacaImageBytes.DecodeChannel"/>, then dispose it.
    /// </summary>
    /// <remarks>
    /// <para><b>The body is streamed into a rented buffer, never buffered by HttpClient.</b> The default
    /// completion buffers the whole response and <c>ReadAsByteArrayAsync</c> then copies it out again,
    /// so every frame cost at least one payload-sized array: 4 to 8.5 MB per guide frame, 52 to 104 MB
    /// per 26 MP sub. Read from <see cref="HttpCompletionOption.ResponseHeadersRead"/>, the body goes
    /// from the connection straight into a buffer from <see cref="ArrayPool{T}.Shared"/>, sized by
    /// Content-Length (grown, for a chunked response), which the caller returns by disposing the
    /// payload.</para>
    /// <para><b>The time budget still covers the body.</b> <see cref="HttpClient.Timeout"/> stops at the
    /// headers once a response is streamed, so the same budget is applied to the whole call here, and
    /// running out of it throws as HttpClient's own timeout does: a <see cref="TaskCanceledException"/>
    /// over a <see cref="TimeoutException"/>, with the caller's token NOT cancelled, which is the only
    /// thing that tells a timeout from a cancellation.</para>
    /// </remarks>
    public async Task<ImageBytesPayload> GetImageArrayBytesAsync(string baseUrl, string deviceType, int deviceNumber, string endpoint, CancellationToken cancellationToken = default)
    {
        var url = BuildGetUrl(baseUrl, deviceType, deviceNumber, endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AlpacaImageBytes.MimeType));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var timeout = httpClient.Timeout;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            budget.CancelAfter(timeout);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, AlpacaImageBytes.MimeType, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Alpaca server returned '{mediaType ?? "(none)"}' for '{endpoint}'; only the binary '{AlpacaImageBytes.MimeType}' transfer is supported (the JSON ImageArray fallback is not implemented).");
            }

            return await ReadIntoRentedBufferAsync(response.Content, budget.Token);
        }
        catch (OperationCanceledException ex) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"The ImageBytes download of '{endpoint}' did not finish within {timeout.TotalSeconds:0.###} s.",
                new TimeoutException(ex.Message, ex));
        }
    }

    // Where a server sends no Content-Length (a chunked body), the rented buffer starts here and doubles.
    private const int InitialChunkedCapacity = 1 << 20;

    /// <summary>
    /// The body straight from the connection into a rented buffer: exactly Content-Length bytes when the
    /// server declares one, which the Alpaca servers do; otherwise a buffer grown until the body ends.
    /// </summary>
    private static async Task<ImageBytesPayload> ReadIntoRentedBufferAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var declared = content.Headers.ContentLength;
        if (declared is { } outOfRange && (outOfRange < 0 || outOfRange > Array.MaxLength))
        {
            throw new NotSupportedException($"ImageBytes Content-Length {outOfRange} is out of range.");
        }

        await using var body = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(declared is { } capacity ? Math.Max((int)capacity, 1) : InitialChunkedCapacity);
        var filled = 0;
        var handedOver = false;
        try
        {
            if (declared is { } exact)
            {
                await body.ReadExactlyAsync(buffer.AsMemory(0, (int)exact), cancellationToken);
                filled = (int)exact;
            }
            else
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(filled), cancellationToken)) > 0)
                {
                    filled += read;
                    if (filled == buffer.Length)
                    {
                        var larger = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                        buffer.AsSpan(0, filled).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }
                }
            }

            handedOver = true;
            return new ImageBytesPayload(buffer, filled);
        }
        finally
        {
            // A read that failed part-way hands nothing over, so the buffer goes back here.
            if (!handedOver)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
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
