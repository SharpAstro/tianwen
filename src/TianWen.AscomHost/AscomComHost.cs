using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Ascom.ComInterop;

namespace TianWen.AscomHost;

/// <summary>
/// The JSON-RPC request handler that exposes <see cref="DispatchObject"/> over the wire, so a CET-enabled
/// parent process can drive a CET-incompatible in-proc ASCOM COM driver hosted here (CET off) as if it
/// were local. The vocabulary mirrors <see cref="DispatchObject"/>'s typed surface one-for-one, so the
/// parent's remote transport (Phase 4) is a mechanical projection of it:
/// <list type="bullet">
///   <item><c>create [progId]</c> -&gt; handle (int)</item>
///   <item><c>release [handle]</c> -&gt; void</item>
///   <item><c>get{Bool,Int,Short,Double,String,DateTime,StringArray,IntArray,Int2DArray} [handle, name]</c> -&gt; value</item>
///   <item><c>set{Bool,Int,Short,Double,String,DateTime} [handle, name, value]</c> -&gt; void</item>
///   <item><c>invoke{,Bool,Int,Double} [handle, name, ...args]</c> -&gt; void|value</item>
/// </list>
/// Every COM-touching operation is marshalled onto the single <see cref="StaComThread"/>, so the handle
/// table needs no synchronization (see that type's remarks).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AscomComHost(StaComThread sta) : IDisposable
{
    private readonly Dictionary<int, DispatchObject> _objects = new();
    private int _nextHandle = 1;

    /// <summary>The <see cref="JsonRpcRequestHandler"/> passed to <see cref="JsonRpcServer"/>.</summary>
    public async ValueTask HandleAsync(string method, JsonElement p, Utf8JsonWriter result, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(method, p, result).ConfigureAwait(false);
        }
        catch (COMException ce)
        {
            // Carry the COM HResult across as the JSON-RPC error code so the parent's driver can still do
            // `catch (COMException) when (HResult == DISP_E_UNKNOWNNAME)` -- the Platform-6 fallback path.
            throw new JsonRpcException(ce.Message, ce.HResult);
        }
    }

    private async ValueTask DispatchAsync(string method, JsonElement p, Utf8JsonWriter result)
    {
        switch (method)
        {
            case "create":
            {
                var progId = Str(p, 0);
                var handle = await sta.InvokeAsync(() =>
                {
                    var obj = new DispatchObject(progId);
                    var h = _nextHandle++;
                    _objects[h] = obj;
                    return h;
                }).ConfigureAwait(false);
                result.WriteNumberValue(handle);
                break;
            }

            case "release":
            {
                var handle = Int(p, 0);
                await sta.InvokeAsync(() =>
                {
                    if (_objects.Remove(handle, out var obj))
                    {
                        obj.Dispose();
                    }
                }).ConfigureAwait(false);
                break;
            }

            // ---- getters: [handle, name] ----
            case "getBool":
                result.WriteBooleanValue(await On(p, o => o.GetBool(Name(p))).ConfigureAwait(false));
                break;
            case "getInt":
                result.WriteNumberValue(await On(p, o => o.GetInt(Name(p))).ConfigureAwait(false));
                break;
            case "getShort":
                result.WriteNumberValue(await On(p, o => o.GetShort(Name(p))).ConfigureAwait(false));
                break;
            case "getDouble":
                result.WriteNumberValue(await On(p, o => o.GetDouble(Name(p))).ConfigureAwait(false));
                break;
            case "getString":
                result.WriteStringValue(await On(p, o => o.GetString(Name(p))).ConfigureAwait(false));
                break;
            case "getDateTime":
                result.WriteStringValue((await On(p, o => o.GetDateTime(Name(p))).ConfigureAwait(false)).ToString("o", CultureInfo.InvariantCulture));
                break;
            case "getStringArray":
                WriteStringArray(result, await On(p, o => o.GetStringArray(Name(p))).ConfigureAwait(false));
                break;
            case "getIntArray":
                WriteIntArray(result, await On(p, o => o.GetIntArray(Name(p))).ConfigureAwait(false));
                break;
            case "getInt2DArray":
                WriteInt2DArray(result, await On(p, o => o.GetInt2DArray(Name(p))).ConfigureAwait(false));
                break;

            // ---- setters: [handle, name, value] ----
            case "setBool":
                await OnVoid(p, o => o.Set(Name(p), p[2].GetBoolean())).ConfigureAwait(false);
                break;
            case "setInt":
                await OnVoid(p, o => o.Set(Name(p), p[2].GetInt32())).ConfigureAwait(false);
                break;
            case "setShort":
                await OnVoid(p, o => o.Set(Name(p), p[2].GetInt16())).ConfigureAwait(false);
                break;
            case "setDouble":
                await OnVoid(p, o => o.Set(Name(p), p[2].GetDouble())).ConfigureAwait(false);
                break;
            case "setString":
                await OnVoid(p, o => o.Set(Name(p), p[2].GetString() ?? string.Empty)).ConfigureAwait(false);
                break;
            case "setDateTime":
            {
                var value = DateTime.Parse(Str(p, 2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                await OnVoid(p, o => o.Set(Name(p), value)).ConfigureAwait(false);
                break;
            }

            // ---- method invocation: [handle, name, ...args] ----
            case "invoke":
                await OnVoid(p, o => o.InvokeMethod(Name(p), Args(p))).ConfigureAwait(false);
                break;
            case "invokeBool":
                result.WriteBooleanValue(await On(p, o => o.InvokeMethodBool(Name(p), Args(p))).ConfigureAwait(false));
                break;
            case "invokeInt":
                result.WriteNumberValue(await On(p, o => o.InvokeMethodInt(Name(p), Args(p))).ConfigureAwait(false));
                break;
            case "invokeDouble":
                result.WriteNumberValue(await On(p, o => o.InvokeMethodDouble(Name(p), Args(p))).ConfigureAwait(false));
                break;

            default:
                throw new JsonRpcException($"unknown method '{method}'");
        }
    }

    // Marshal a value-returning COM op onto the STA thread; the closure resolves the handle there too so
    // the handle table is only ever touched on that one thread.
    private Task<T> On<T>(JsonElement p, Func<DispatchObject, T> op)
    {
        var handle = Int(p, 0);
        return sta.InvokeAsync(() => op(Resolve(handle)));
    }

    private Task OnVoid(JsonElement p, Action<DispatchObject> op)
    {
        var handle = Int(p, 0);
        return sta.InvokeAsync(() => op(Resolve(handle)));
    }

    private DispatchObject Resolve(int handle)
        => _objects.TryGetValue(handle, out var obj) ? obj : throw new JsonRpcException($"unknown handle {handle}");

    private static int Int(JsonElement p, int i) => p[i].GetInt32();

    private static string Str(JsonElement p, int i)
        => p[i].GetString() ?? throw new JsonRpcException($"argument {i} must be a string");

    private static string Name(JsonElement p) => Str(p, 1);

    // ASCOM method arguments are almost always numeric/bool. JSON loses the int-vs-double distinction, so
    // integral numbers map to int and fractional ones to double (DispatchObject.ArgsToVariants accepts
    // bool/int/short/double/string/DateTime). Rare DateTime args would need a tagged encoding -- deferred.
    private static object[] Args(JsonElement p)
    {
        var len = p.GetArrayLength();
        if (len <= 2)
        {
            return [];
        }

        var args = new object[len - 2];
        for (var i = 2; i < len; i++)
        {
            args[i - 2] = ToArg(p[i]);
        }
        return args;
    }

    private static object ToArg(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Number => e.TryGetInt32(out var i) ? i : e.GetDouble(),
        _ => throw new JsonRpcException($"unsupported argument kind {e.ValueKind}"),
    };

    private static void WriteStringArray(Utf8JsonWriter w, string[] arr)
    {
        w.WriteStartArray();
        foreach (var s in arr)
        {
            w.WriteStringValue(s);
        }
        w.WriteEndArray();
    }

    private static void WriteIntArray(Utf8JsonWriter w, int[] arr)
    {
        w.WriteStartArray();
        foreach (var n in arr)
        {
            w.WriteNumberValue(n);
        }
        w.WriteEndArray();
    }

    // Emitted as an array-of-rows [[...],[...]] preserving the native [dim1, dim2] shape.
    private static void WriteInt2DArray(Utf8JsonWriter w, int[,] arr)
    {
        var d0 = arr.GetLength(0);
        var d1 = arr.GetLength(1);
        w.WriteStartArray();
        for (var i = 0; i < d0; i++)
        {
            w.WriteStartArray();
            for (var j = 0; j < d1; j++)
            {
                w.WriteNumberValue(arr[i, j]);
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    public void Dispose()
    {
        // Best-effort release on the STA thread the objects were created on, fire-and-forget: the process
        // is shutting down and the STA thread is a background thread, so we don't block on it (and mustn't
        // sync-over-async). Explicit per-object lifetime goes through the "release" RPC; this is the net
        // for anything still open at shutdown. StaComThread.Dispose() drains this queued item before exit.
        _ = sta.InvokeAsync(() =>
        {
            foreach (var obj in _objects.Values)
            {
                obj.Dispose();
            }
            _objects.Clear();
        });
    }
}
