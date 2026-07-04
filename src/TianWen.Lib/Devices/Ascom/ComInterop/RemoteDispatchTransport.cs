using System;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// An <see cref="IDispatchTransport"/> that drives a COM object living in an out-of-process CET-off
/// <c>tianwen-ascomhost</c> helper. Each typed call is projected onto the helper's JSON-RPC vocabulary
/// (<c>get*</c>/<c>set*</c>/<c>invoke*</c>, keyed by an object handle) — the mechanical mirror of
/// <see cref="AscomComHost"/>'s handler. Used only for CET-incompatible in-proc .NET Framework drivers
/// (see <see cref="AscomComServerClassifier"/>); every other driver uses <see cref="DispatchObject"/>
/// in-proc. See docs/plans/ascom-oop-host.md.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RemoteDispatchTransport : IDispatchTransport
{
    private readonly AscomHostProcess _host;
    private readonly int _handle;
    private bool _disposed;

    private RemoteDispatchTransport(AscomHostProcess host, int handle)
    {
        _host = host;
        _handle = handle;
    }

    /// <summary>Spawns the helper, connects, and creates the COM object for <paramref name="progId"/>.</summary>
    public static RemoteDispatchTransport Create(string exePath, string progId, TimeSpan handshakeTimeout)
    {
        var host = AscomHostProcess.Spawn(exePath, handshakeTimeout);
        try
        {
            int handle;
            using (var response = host.Call("create", w =>
            {
                w.WriteStartArray();
                w.WriteStringValue(progId);
                w.WriteEndArray();
            }))
            {
                handle = response.RootElement.GetProperty("result").GetInt32();
            }
            return new RemoteDispatchTransport(host, handle);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    // ---- getters ----
    public bool GetBool(string name) => Scalar("getBool", name, static r => r.GetBoolean());
    public int GetInt(string name) => Scalar("getInt", name, static r => r.GetInt32());
    public short GetShort(string name) => Scalar("getShort", name, static r => r.GetInt16());
    public double GetDouble(string name) => Scalar("getDouble", name, static r => r.GetDouble());
    public string GetString(string name) => Scalar("getString", name, static r => r.GetString() ?? string.Empty);
    public DateTime GetDateTime(string name) => Scalar("getDateTime", name,
        static r => DateTime.Parse(r.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public string[] GetStringArray(string name) => Scalar("getStringArray", name, static r =>
    {
        var arr = new string[r.GetArrayLength()];
        var i = 0;
        foreach (var e in r.EnumerateArray())
        {
            arr[i++] = e.GetString() ?? string.Empty;
        }
        return arr;
    });

    public int[] GetIntArray(string name) => Scalar("getIntArray", name, static r =>
    {
        var arr = new int[r.GetArrayLength()];
        var i = 0;
        foreach (var e in r.EnumerateArray())
        {
            arr[i++] = e.GetInt32();
        }
        return arr;
    });

    public int[,] GetInt2DArray(string name) => Scalar("getInt2DArray", name, static r =>
    {
        var rows = r.GetArrayLength();
        var cols = rows > 0 ? r[0].GetArrayLength() : 0;
        var arr = new int[rows, cols];
        var i = 0;
        foreach (var row in r.EnumerateArray())
        {
            var j = 0;
            foreach (var cell in row.EnumerateArray())
            {
                arr[i, j++] = cell.GetInt32();
            }
            i++;
        }
        return arr;
    });

    public object? GetObject(string name)
        => throw new NotSupportedException($"Reading opaque VARIANT property '{name}' over the out-of-process ASCOM host is not supported.");

    // ---- setters ----
    public void Set(string name, bool value) => SetValue("setBool", name, w => w.WriteBooleanValue(value));
    public void Set(string name, int value) => SetValue("setInt", name, w => w.WriteNumberValue(value));
    public void Set(string name, short value) => SetValue("setShort", name, w => w.WriteNumberValue(value));
    public void Set(string name, double value) => SetValue("setDouble", name, w => w.WriteNumberValue(value));
    public void Set(string name, string value) => SetValue("setString", name, w => w.WriteStringValue(value));
    public void Set(string name, DateTime value) => SetValue("setDateTime", name, w => w.WriteStringValue(value.ToString("o", CultureInfo.InvariantCulture)));

    // ---- invokes ----
    public void InvokeMethod(string name) => Invoke("invoke", name, []).Dispose();
    public void InvokeMethod(string name, params object[] args) => Invoke("invoke", name, args).Dispose();
    public bool InvokeMethodBool(string name, params object[] args) => Invoke("invokeBool", name, args).ReadResult(static r => r.GetBoolean());
    public int InvokeMethodInt(string name, params object[] args) => Invoke("invokeInt", name, args).ReadResult(static r => r.GetInt32());
    public double InvokeMethodDouble(string name, params object[] args) => Invoke("invokeDouble", name, args).ReadResult(static r => r.GetDouble());

    public object? InvokeMethodObject(string name, params object[] args)
        => throw new NotSupportedException($"Invoking '{name}' with an opaque VARIANT return over the out-of-process ASCOM host is not supported.");

    // Sub-dispatch (telescope AxisRates / collection Item) needs handle-returning support in the helper,
    // deferred to the Phase 5 mount generalization. No crash-cluster device (cover/focuser/FW/switch) uses it.
    public IDispatchTransport InvokeMethodDispatch(string name, params object[] args)
        => throw new NotSupportedException($"Sub-dispatch method '{name}' over the out-of-process ASCOM host is not yet supported (Phase 5).");

    public IDispatchTransport GetPropertyDispatch(string name, params object[] args)
        => throw new NotSupportedException($"Sub-dispatch property '{name}' over the out-of-process ASCOM host is not yet supported (Phase 5).");

    private T Scalar<T>(string method, string name, Func<JsonElement, T> read)
    {
        using var response = _host.Call(method, w => WriteHandleName(w, name));
        return read(response.RootElement.GetProperty("result"));
    }

    private void SetValue(string method, string name, Action<Utf8JsonWriter> writeValue)
    {
        using var response = _host.Call(method, w =>
        {
            w.WriteStartArray();
            w.WriteNumberValue(_handle);
            w.WriteStringValue(name);
            writeValue(w);
            w.WriteEndArray();
        });
    }

    private JsonDocument Invoke(string method, string name, object[] args) => _host.Call(method, w =>
    {
        w.WriteStartArray();
        w.WriteNumberValue(_handle);
        w.WriteStringValue(name);
        foreach (var arg in args)
        {
            WriteArg(w, arg);
        }
        w.WriteEndArray();
    });

    private void WriteHandleName(Utf8JsonWriter w, string name)
    {
        w.WriteStartArray();
        w.WriteNumberValue(_handle);
        w.WriteStringValue(name);
        w.WriteEndArray();
    }

    private static void WriteArg(Utf8JsonWriter w, object arg)
    {
        switch (arg)
        {
            case bool b: w.WriteBooleanValue(b); break;
            case int n: w.WriteNumberValue(n); break;
            case short s: w.WriteNumberValue(s); break;
            case double d: w.WriteNumberValue(d); break;
            case string str: w.WriteStringValue(str); break;
            case DateTime dt: w.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture)); break;
            default: throw new ArgumentException($"Unsupported ASCOM argument type: {arg?.GetType()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            using (_host.Call("release", w =>
            {
                w.WriteStartArray();
                w.WriteNumberValue(_handle);
                w.WriteEndArray();
            })) { }
        }
        catch
        {
            // host may already be gone; the host dispose below kills it regardless
        }

        _host.Dispose();
    }
}

file static class RemoteDispatchExtensions
{
    // Reads the "result" of a call document and disposes it in one expression.
    public static T ReadResult<T>(this JsonDocument document, Func<JsonElement, T> read)
    {
        using (document)
        {
            return read(document.RootElement.GetProperty("result"));
        }
    }
}
