using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Owns one spawned <c>tianwen-ascomhost</c> helper process, its loopback TCP connection, and the
/// synchronous JSON-RPC request/response over it. One helper hosts one COM object.
/// <para>
/// Calls are <b>synchronous</b> and single-flight (serialized by <see cref="_gate"/>): the helper serves
/// requests sequentially and never sends unsolicited messages, so the reply to request N is the next
/// line — no async receive loop / id-correlation dictionary is needed (unlike the event-pushing PHD2
/// client). This matches the inherently synchronous, single-apartment ASCOM COM surface the transport
/// replaces (a blocking COM call becomes a blocking loopback round-trip).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AscomHostProcess : IDisposable
{
    private static readonly ReadOnlyMemory<byte> CRLF = "\r\n"u8.ToArray();

    private readonly Process _process;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly Lock _gate = new(); // serialize the single-flight request/response on the shared socket; never on a render thread (driver calls are offloaded)
    private long _id;
    private bool _disposed;

    private AscomHostProcess(Process process, TcpClient tcp)
    {
        _process = process;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream);
    }

    /// <summary>
    /// Spawns the helper, reads its <c>PORT n</c> handshake line, and connects to the loopback port.
    /// Assigns the child to the kill-on-close job so it can't orphan us.
    /// </summary>
    public static AscomHostProcess Spawn(string exePath, TimeSpan handshakeTimeout)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();

        try
        {
            AscomHostJob.TryAssign(process.Handle);

            // The helper prints "PORT <n>" as its first stdout line once the socket is bound. Bound the
            // wait so a helper that dies before printing (or hangs) doesn't block the caller forever.
            var handshake = ReadHandshake(process, handshakeTimeout);
            if (handshake is null || !handshake.StartsWith("PORT ", StringComparison.Ordinal)
                || !int.TryParse(handshake.AsSpan("PORT ".Length), out var port))
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new IOException($"ASCOM host did not report a port (got '{handshake}'). stderr: {stderr}");
            }

            var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            return new AscomHostProcess(process, tcp);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    private static string? ReadHandshake(Process process, TimeSpan timeout)
    {
        // ReadLineAsync().Wait(timeout) would leak the read; instead read on a background thread and
        // bound the wait. The helper prints the line within milliseconds of a successful start.
        string? line = null;
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { line = process.StandardOutput.ReadLine(); }
            catch { /* process died; line stays null */ }
            finally { done.Set(); }
        })
        { IsBackground = true, Name = "ascomhost-handshake" };
        thread.Start();
        done.Wait(timeout);
        return line;
    }

    /// <summary>
    /// Sends a JSON-RPC request and returns the correlated response document (caller reads <c>result</c>
    /// and disposes it). Throws <see cref="COMException"/> carrying the peer's HResult on an <c>error</c>
    /// response, so the driver's <c>catch (COMException) when (HResult == DISP_E_UNKNOWNNAME)</c>
    /// Platform-6 fallback works across the wire.
    /// </summary>
    public JsonDocument Call(string method, Action<Utf8JsonWriter>? writeParams)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var id = ++_id;
            var buffer = new ArrayBufferWriter<byte>(128);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("method", method);
                writer.WriteNumber("id", id);
                if (writeParams is not null)
                {
                    writer.WritePropertyName("params");
                    writeParams(writer);
                }
                writer.WriteEndObject();
            }

            WriteLine(buffer.WrittenMemory);

            var line = _reader.ReadLine() ?? throw new IOException("ASCOM host closed the connection");
            var response = JsonDocument.Parse(line);

            var root = response.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind is JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : error.ToString();
                var code = error.ValueKind is JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)
                    ? ci
                    : 0;
                response.Dispose();
                throw code != 0
                    ? new COMException(message ?? "ASCOM host error", code)
                    : new InvalidOperationException(message ?? "ASCOM host error");
            }

            if (!root.TryGetProperty("id", out var respId) || !respId.TryGetInt64(out var rid) || rid != id)
            {
                var actual = root.ToString();
                response.Dispose();
                throw new IOException($"ASCOM host response id was not {id}: {actual}");
            }

            return response;
        }
    }

    private void WriteLine(ReadOnlyMemory<byte> jsonUtf8)
    {
        _stream.Write(jsonUtf8.Span);
        _stream.Write(CRLF.Span);
        _stream.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        // Closing the socket makes the helper's server loop see EOF and exit; the job object + explicit
        // kill are the backstops.
        try { _reader.Dispose(); } catch { /* ignore */ }
        try { _tcp.Dispose(); } catch { /* ignore */ }
        TryKill(_process);
        _process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // already gone / not started
        }
    }
}
