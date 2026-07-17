/*

Portions derived from the phd2client (Andy Galasso), MIT License:

Copyright (c) 2018 Andy Galasso

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

/// <summary>
/// Generic JSON-RPC peer over an <see cref="IUtf8TextBasedConnection"/> (one line == one message).
/// Owns request-<c>id</c> correlation, the receive loop, and response-vs-notification routing; the
/// caller supplies message-specific concerns via delegates (parameter serialization, notification
/// handling, error reporting). Extracted from the PHD2 guider driver so the same client backs PHD2
/// and the out-of-process ASCOM COM host.
/// <para>
/// Wire shape (matches PHD2): a request is <c>{"method":…,"id":N,"params":…?}</c>; an inbound message
/// carrying both a <c>jsonrpc</c> property and a numeric <c>id</c> is a <b>response</b> (correlated by
/// id), anything else is a server <b>notification</b> handed to <c>onNotification</c>.
/// </para>
/// <para>
/// One client wraps one connection for its whole lifetime; reconnect creates a new client. The caller
/// runs <see cref="ReceiveLoopAsync"/> as a background task and awaits <see cref="CallAsync"/> from any
/// number of callers concurrently (each is correlated by its own id).
/// </para>
/// </summary>
internal sealed class JsonRpcClient(
    IUtf8TextBasedConnection connection,
    Func<JsonDocument, CancellationToken, ValueTask>? onNotification = null,
    Action<Exception, string>? onError = null) : IDisposable
{
    // Correlation is one TaskCompletionSource per in-flight id, registered BEFORE the request is
    // written. The previous design (a responses-by-id dictionary + a shared auto-reset event) had
    // a lost-wakeup race: DotNext's AsyncManualResetEvent.Set(autoReset: true) does NOT latch when
    // no waiter is suspended yet, so a response landing between the caller's dictionary miss and
    // its WaitAsync stranded the call forever (the CI linux-arm hang in
    // JsonRpcServerTests.GivenServerAndClientOverNamedPipe...). With a pre-registered TCS the
    // receive loop always finds the waiter, whatever the interleaving.
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = [];
    private long _messageId;

    /// <summary>
    /// Reads and dispatches inbound messages until the connection closes (<c>ReadLineAsync</c> returns
    /// null), <paramref name="cancellationToken"/> fires, or a read faults. Run as a background task.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        string? line = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
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
                    // A read fault means the connection is unusable -- report and end the loop so the
                    // owner can tear down / reconnect (the original PHD2 loop fell through here with a
                    // stale `line` and could reprocess it; ending is both cleaner and reconnect-friendly).
                    onError?.Invoke(ex, "reading from input stream");
                    break;
                }

                if (line is null)
                {
                    break; // peer disconnected
                }

                JsonDocument message;
                try
                {
                    message = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    onError?.Invoke(ex, $"ignoring invalid json: {line}");
                    continue;
                }

                if (message.RootElement.TryGetProperty("jsonrpc", out _)
                    && message.RootElement.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind is JsonValueKind.Number
                    && idProp.TryGetInt64(out var id))
                {
                    // Response: hand the document to the awaiting caller. No caller (cancelled and
                    // already cleaned up, or an id the peer invented) -> the document is ours to drop.
                    if (!_pending.TryRemove(id, out var pending) || !pending.TrySetResult(message))
                    {
                        message.Dispose();
                    }
                }
                else if (onNotification is { } handler)
                {
                    // Notification: the handler owns the document.
                    await handler(message, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    message.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex, $"processing: {line}");
        }
        finally
        {
            // The loop is the only response producer - once it ends (peer disconnect, fault,
            // cancellation), an in-flight CallAsync could never complete. Fail them all instead of
            // letting them hang.
            FailAllPending(new JsonRpcException("Connection closed before a response arrived"));
        }
    }

    private void FailAllPending(Exception reason)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var pending))
            {
                pending.TrySetException(reason);
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="method"/> with an auto-assigned id and awaits the correlated response.
    /// <paramref name="writeParams"/>, when non-null, writes the <c>params</c> value (array or object);
    /// pass null for a parameterless call. Throws <see cref="JsonRpcException"/> on a send failure or an
    /// <c>error</c> response. The returned <see cref="JsonDocument"/> is owned by the caller (dispose it).
    /// </summary>
    public async ValueTask<JsonDocument> CallAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        var id = Interlocked.Increment(ref _messageId);
        using (var req = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            req.WriteStartObject();
            req.WriteString("method", method);
            req.WriteNumber("id", id);
            if (writeParams is not null)
            {
                req.WritePropertyName("params");
                writeParams(req);
            }
            req.WriteEndObject();
        }

        // Register BEFORE sending: however fast the response races back, the receive loop finds
        // the completion source. RunContinuationsAsynchronously keeps the loop off our stack.
        var pending = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        JsonDocument response;
        try
        {
            if (!await connection.WriteLineAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false))
            {
                throw new JsonRpcException($"Failed to send request '{method}'");
            }

            using var reg = cancellationToken.Register(
                static state => ((TaskCompletionSource<JsonDocument>)state!).TrySetCanceled(), pending);
            response = await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            // Cancel/fault path: withdraw the registration so a late response is disposed by the
            // receive loop instead of completing an abandoned source. No-op on success (the loop
            // already removed it).
            _pending.TryRemove(id, out _);
        }

        if (response.RootElement.TryGetProperty("error", out var errorEl))
        {
            var message = errorEl.ValueKind is JsonValueKind.Object && errorEl.TryGetProperty("message", out var m)
                ? m.GetString()
                : errorEl.ToString();
            var code = errorEl.ValueKind is JsonValueKind.Object && errorEl.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)
                ? ci
                : (int?)null;
            response.Dispose();
            throw new JsonRpcException(message ?? "error response did not contain a message", code);
        }

        return response;
    }

    public void Dispose()
    {
        FailAllPending(new JsonRpcException("Client disposed"));
    }
}
