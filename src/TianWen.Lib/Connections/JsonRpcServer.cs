using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

/// <summary>
/// Handles one JSON-RPC request. Writes the <c>result</c> <b>value</b> into <paramref name="resultWriter"/>
/// (e.g. <c>resultWriter.WriteBooleanValue(true)</c>, or an object/array); write nothing for a void
/// method (the server emits <c>"result":null</c>). Throw <see cref="JsonRpcException"/> (or any exception)
/// to have the server emit an <c>error</c> object instead. <paramref name="params"/> is
/// <see cref="JsonValueKind.Undefined"/> when the request carried no <c>params</c>.
/// </summary>
internal delegate ValueTask JsonRpcRequestHandler(string method, JsonElement @params, Utf8JsonWriter resultWriter, CancellationToken cancellationToken);

/// <summary>
/// Minimal JSON-RPC 2.0 server over loopback TCP: binds an auto-assigned loopback port, accepts a
/// single client, and serves requests through a <see cref="JsonRpcRequestHandler"/> until the client
/// disconnects or cancellation fires. The server side of the same wire format
/// <see cref="JsonRpcClient"/> speaks, so the two interoperate directly (client's
/// <c>{"method","id","params"?}</c> in, this server's <c>{"jsonrpc":"2.0","id",result|error}</c> out).
/// <para>
/// Used by the out-of-process ASCOM COM host (one server per hosted device, on 127.0.0.1). Requests
/// are served sequentially on the accept loop, which is exactly what a single COM object wants.
/// </para>
/// </summary>
internal sealed class JsonRpcServer(JsonRpcRequestHandler handler, Action<Exception, string>? onError = null) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private bool _started;

    /// <summary>Binds and starts listening on an OS-assigned loopback port; returns that port.</summary>
    public int Start()
    {
        _listener.Start();
        _started = true;
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Accepts one client and serves its requests until it disconnects or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            Start();
        }

        using var tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using var connection = new JsonRpcOverTcpConnection(tcp);
        await ServeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeAsync(IUtf8TextBasedConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex, "reading request");
                break;
            }

            if (line is null)
            {
                break; // client disconnected
            }

            JsonDocument request;
            try
            {
                request = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                onError?.Invoke(ex, $"ignoring invalid json: {line}");
                continue;
            }

            using (request)
            {
                var root = request.RootElement;
                var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                var id = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var i) ? i : 0L;
                var @params = root.TryGetProperty("params", out var p) ? p : default;

                // Dispatch into a scratch buffer so a mid-write fault can't corrupt the response frame.
                var result = new ArrayBufferWriter<byte>(64);
                string? errorMessage = null;
                int? errorCode = null;
                try
                {
                    using var resultWriter = new Utf8JsonWriter(result, new JsonWriterOptions { Indented = false });
                    await handler(method, @params, resultWriter, cancellationToken).ConfigureAwait(false);
                    resultWriter.Flush();
                }
                catch (JsonRpcException jre)
                {
                    errorMessage = jre.Message;
                    errorCode = jre.Code;
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex, $"handling '{method}'");
                    errorMessage = ex.Message;
                }

                var response = new ArrayBufferWriter<byte>(128);
                using (var w = new Utf8JsonWriter(response, new JsonWriterOptions { Indented = false }))
                {
                    w.WriteStartObject();
                    w.WriteString("jsonrpc", "2.0");
                    w.WriteNumber("id", id);
                    if (errorMessage is null)
                    {
                        w.WritePropertyName("result");
                        if (result.WrittenCount > 0)
                        {
                            w.WriteRawValue(result.WrittenSpan, skipInputValidation: true);
                        }
                        else
                        {
                            w.WriteNullValue();
                        }
                    }
                    else
                    {
                        w.WritePropertyName("error");
                        w.WriteStartObject();
                        if (errorCode is { } code)
                        {
                            w.WriteNumber("code", code);
                        }
                        w.WriteString("message", errorMessage);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }

                await connection.WriteLineAsync(response.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _listener.Stop();
}
