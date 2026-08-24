using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Owns one spawned <c>tianwen-ascomhost</c> helper process, its named-pipe connection, and the
/// synchronous JSON-RPC request/response over it. One helper hosts one COM object.
/// <para>
/// The transport is a per-user named pipe (no network stack / loopback port / firewall involvement). To
/// avoid a create-vs-connect race, the <b>parent owns the pipe server</b>: it creates the server pipe
/// with a GUID name, passes that name to the helper as a launch argument, spawns it, then waits for the
/// helper (the pipe client) to connect.
/// </para>
/// <para>
/// Calls are <b>synchronous</b> and single-flight (serialized by <see cref="_gate"/>): the helper serves
/// requests sequentially and never sends unsolicited messages, so the reply to request N is the next
/// line: no async receive loop / id-correlation dictionary is needed (unlike the event-pushing PHD2
/// client). This matches the inherently synchronous, single-apartment ASCOM COM surface the transport
/// replaces (a blocking COM call becomes a blocking pipe round-trip).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AscomHostProcess : IDisposable
{
    private static readonly ReadOnlyMemory<byte> CRLF = "\r\n"u8.ToArray();

    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly Lock _gate = new(); // serialize the single-flight request/response on the shared pipe; never on a render thread (driver calls are offloaded)
    private long _id;
    private bool _disposed;

    private AscomHostProcess(Process process, NamedPipeServerStream pipe)
    {
        _process = process;
        _pipe = pipe;
        _reader = new StreamReader(pipe);
    }

    /// <summary>
    /// Creates the server pipe, spawns the helper (passing the pipe name), and waits for it to connect.
    /// Assigns the child to the kill-on-close job so it can't orphan us.
    /// </summary>
    public static AscomHostProcess Spawn(string exePath, TimeSpan connectTimeout)
    {
        var pipeName = $"tianwen-ascomhost-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(pipeName);
            process.Start();
            AscomHostJob.TryAssign(process.Handle);

            if (!WaitForConnection(pipe, connectTimeout))
            {
                // Kill BEFORE reading stderr: ReadToEnd blocks until the process exits, so a helper
                // that hangs without connecting (rather than crashing) used to wedge the spawn here
                // forever -- found by the never-connects test, which sat in this line for its whole
                // 30s budget. A crashed helper is unaffected: it has already exited, and its stderr
                // is intact either way.
                TryKill(process);
                var stderr = process.StandardError.ReadToEnd();
                throw new IOException($"ASCOM host did not connect to pipe '{pipeName}' within {connectTimeout}. stderr: {stderr}");
            }

            return new AscomHostProcess(process, pipe);
        }
        catch
        {
            pipe.Dispose(); // unblocks the accept thread if it is still waiting
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
            throw;
        }
    }

    private static bool WaitForConnection(NamedPipeServerStream pipe, TimeSpan timeout)
    {
        // WaitForConnection has no timeout overload and a synchronous accept cannot be cancelled, so
        // run it on a background thread and bound the wait with Thread.Join -- which needs no separate
        // completion event (an earlier ManualResetEventSlim here was redundant with Join and was the
        // one disposable that could be neither disposed nor fixed, only suppressed). Join is also the
        // memory barrier that makes `connected` safe to read.
        //
        // On timeout the caller's failure path disposes the pipe, which throws the accept out and
        // ends the thread. InstanceGate (SharpAstro.AppShell) instead wakes its accepter by connecting
        // to itself, because a GATE must keep its pipe alive across the wake; a one-shot spawn that is
        // about to tear the pipe down has no such constraint, so the simpler shutdown is the right one.
        var connected = false;
        var thread = new Thread(() =>
        {
            try
            {
                pipe.WaitForConnection();
                connected = true;
            }
            catch
            {
                // pipe disposed / connection failed; connected stays false
            }
        })
        { IsBackground = true, Name = "ascomhost-pipe-accept" };
        thread.Start();
        return thread.Join(timeout) && connected;
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
        _pipe.Write(jsonUtf8.Span);
        _pipe.Write(CRLF.Span);
        _pipe.Flush();
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

        // Disposing the pipe makes the helper's serve loop see EOF and exit; the job object + explicit
        // kill are the backstops.
        try { _reader.Dispose(); } catch { /* ignore */ }
        try { _pipe.Dispose(); } catch { /* ignore */ }
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
