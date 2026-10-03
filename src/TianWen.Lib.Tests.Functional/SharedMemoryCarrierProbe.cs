using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Diagnostic probe, not an assertion: what one live frame costs to carry through each carrier, shared memory and the
/// socket, from a node on its own socket streaming the fake camera at its full sensor (P4b of
/// docs/plans/hardware-in-the-server.md, #932). Each read is timed only once the node already holds a frame the stream
/// has not sent, so the time is the carry alone, never the wait for the camera (which renders a full synthetic frame in
/// seconds).
/// </summary>
/// <remarks>Set <c>TIANWEN_CARRIER_PROBE=1</c> to run it; its numbers are for a person to read, never a gate on CI.</remarks>
[Collection("Hosting")]
public class SharedMemoryCarrierProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_CARRIER_PROBE";
    private const int Frames = 8;
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);

    [Fact(Timeout = 600_000)]
    public async Task OneLiveFrameAtFullSensorThroughEachCarrier()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is "1", $"{EnvVar} not set to 1");
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct,
            onItsSocket: true);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        await using var window = await NodeWindow.OpenAsync(node, output, ct);
        var client = new TianWenNodeClient(node.Client);
        var started = await client.StartPlanetaryAsync(new PlanetaryRequestDto { ExposureMs = 1, RoiWidth = 16384, RoiHeight = 16384 }, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        var frames = node.App.Services.GetRequiredService<NodeFrames>();
        try
        {
            foreach (var sharedMemory in new[] { true, false, true, false })
            {
                await MeasureAsync(node.Transport, frames, sharedMemory, ct);
            }
        }
        finally
        {
            await client.StopPlanetaryAsync(ct);
        }
    }

    private async Task MeasureAsync(NodeTransport transport, NodeFrames frames, bool sharedMemory, CancellationToken ct)
    {
        await using var stream = await transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, sharedMemory, ct);
        var reader = new FrameReader();
        var (last, warm) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull("the stream is open while the capture runs");
        (int Width, int Height, int Channels, bool Packed) shape = (warm.Width, warm.Height, warm.ChannelCount, FrameWire.PacksAsUInt16(warm));
        warm.Release();

        var carries = new List<double>(Frames);
        for (var i = 0; i < Frames; i++)
        {
            // A frame the stream has not sent is on show: the ask is answered at once, and its time is the carry.
            while (frames.Named(FrameSources.PlanetaryLive).Number == last)
            {
                await Task.Delay(5, ct);
            }
            var one = Stopwatch.StartNew();
            var (number, frame) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull();
            carries.Add(one.Elapsed.TotalMilliseconds);
            last = number;
            frame.Release();
        }

        var megabytes = shape.Width * (double)shape.Height * shape.Channels * (shape.Packed ? 2 : 4) / 1e6;
        var sorted = carries.Order().ToArray();
        output.WriteLine($"{(sharedMemory ? "shared memory" : "socket       ")}: {shape.Width}x{shape.Height}x{shape.Channels} "
            + $"{(shape.Packed ? "16-bit" : "float")} ({megabytes:F1} MB), a carry min {sorted[0]:F1} ms, "
            + $"p50 {sorted[sorted.Length / 2]:F1} ms, max {sorted[^1]:F1} ms; {stream.FramesFromSharedMemory} from slots, "
            + $"{stream.TornFrames} torn");
    }
}
