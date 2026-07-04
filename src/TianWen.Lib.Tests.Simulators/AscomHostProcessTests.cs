using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// End-to-end proof of the out-of-process ASCOM COM host (<c>tianwen-ascomhost.exe</c>): spawn the real
/// helper, read its <c>PORT n</c> handshake line, connect the production <see cref="JsonRpcClient"/> over
/// loopback TCP, and round-trip <c>create</c> / <c>getInt</c> / <c>setInt</c> / <c>release</c> against a
/// real COM object. Uses <c>Scripting.Dictionary</c> (scrrun.dll, present on every Windows install) as a
/// benign always-available <c>IDispatch</c>, so this exercises the whole transport + the raw-vtable
/// <c>DispatchObject</c> against genuine COM without needing an ASCOM Platform install or hardware.
/// <para>Auto-skips off Windows or when the helper exe hasn't been built, so a bare <c>dotnet test</c>
/// stays green.</para>
/// </summary>
public class AscomHostProcessTests
{
    [Fact]
    public async Task GivenTheBuiltHelperWhenDrivingARealComObjectThenCreateGetSetReleaseRoundTrip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The ASCOM COM host is Windows-only.");

        var exe = LocateHelperExe();
        Assert.SkipUnless(exe is not null, "Skipped unless tianwen-ascomhost.exe has been built.");

        var ct = TestContext.Current.CancellationToken;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe!)
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
            // Handshake: the helper prints "PORT <n>" as its first stdout line once the socket is bound.
            var handshake = await process.StandardOutput.ReadLineAsync(ct);
            handshake.ShouldNotBeNull();
            handshake!.ShouldStartWith("PORT ");
            var port = int.Parse(handshake.AsSpan("PORT ".Length));
            port.ShouldBeGreaterThan(0);

            var connection = new JsonRpcOverTcpConnection();
            await connection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            using var client = new JsonRpcClient(connection);
            _ = client.ReceiveLoopAsync(ct);

            // create Scripting.Dictionary -> handle
            int handle;
            using (var r = await client.CallAsync("create", w =>
            {
                w.WriteStartArray();
                w.WriteStringValue("Scripting.Dictionary");
                w.WriteEndArray();
            }, ct))
            {
                handle = r.RootElement.GetProperty("result").GetInt32();
            }
            handle.ShouldBeGreaterThan(0);

            // getInt Count -> 0 (a freshly created dictionary is empty)
            using (var r = await client.CallAsync("getInt", w => WriteHandleName(w, handle, "Count"), ct))
            {
                r.RootElement.GetProperty("result").GetInt32().ShouldBe(0);
            }

            // setInt CompareMode = 1 then getInt CompareMode -> 1 (proves the set path round-trips)
            using (await client.CallAsync("setInt", w =>
            {
                w.WriteStartArray();
                w.WriteNumberValue(handle);
                w.WriteStringValue("CompareMode");
                w.WriteNumberValue(1);
                w.WriteEndArray();
            }, ct)) { }

            using (var r = await client.CallAsync("getInt", w => WriteHandleName(w, handle, "CompareMode"), ct))
            {
                r.RootElement.GetProperty("result").GetInt32().ShouldBe(1);
            }

            // release -> void (result:null)
            using (var r = await client.CallAsync("release", w =>
            {
                w.WriteStartArray();
                w.WriteNumberValue(handle);
                w.WriteEndArray();
            }, ct))
            {
                r.RootElement.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);
            }

            connection.Dispose();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static void WriteHandleName(Utf8JsonWriter w, int handle, string name)
    {
        w.WriteStartArray();
        w.WriteNumberValue(handle);
        w.WriteStringValue(name);
        w.WriteEndArray();
    }

    /// <summary>
    /// Resolves the sibling helper exe by walking up to the solution root (the dir with TianWen.slnx),
    /// then into TianWen.AscomHost's build output for the same configuration as this test assembly.
    /// </summary>
    private static string? LocateHelperExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TianWen.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            return null;
        }

        var config = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var exe = Path.Combine(dir.FullName, "TianWen.AscomHost", "bin", config, "net10.0-windows", "tianwen-ascomhost.exe");
        return File.Exists(exe) ? exe : null;
    }
}
