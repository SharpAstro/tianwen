using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Diagnostic probe, not an assertion: how fast a local client sees a REAL camera's live frames through a running node,
/// the measurement P4b (#932, shared-memory frames) starts from. It drives the node's planetary capture, as a daytime
/// pre-focus does, and reports three rates side by side: what the camera delivers (the node's own count), what a client
/// reads through the frame stream, and what one frame costs to carry when it is already there (a
/// <c>/frames/.../latest</c> fetch with nothing to wait for).
/// </summary>
/// <remarks>
/// Point <c>TIANWEN_LIVE_FRAME_PROBE</c> at the socket of a node whose active profile's OTA 0 has the camera, and hold no
/// run on it. The probe is the only client, and beats as a window does, or the capture would stop at its detach grace.
/// </remarks>
[Collection("Hosting")]
public class LiveFrameRateProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_LIVE_FRAME_PROBE";

    /// <summary>Frames read through the stream once it is warm.</summary>
    private const int Frames = 150;

    [Theory(Timeout = 300_000)]
    [InlineData(640, 320)]
    [InlineData(1920, 1080)]
    [InlineData(4096, 4096)]
    public async Task ALocalClientReadsTheLiveFrameAtTheRateTheCarrierAllows(int roiWidth, int roiHeight)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set to a node's socket");
        var socket = Environment.GetEnvironmentVariable(EnvVar) ?? "";
        var ct = TestContext.Current.CancellationToken;

        var transport = NodeTransport.OverSocket(socket);
        using var http = transport.CreateHttpClient();
        var client = new TianWenNodeClient(http);
        await using var window = NodeWindow.Open(transport, output, ct);
        await window.UntilConnectedAsync(ct);

        // The camera is OTA 0's in the node's active profile, connected through the node as a window connects it.
        var active = (await client.GetActiveProfileAsync(ct)).Value.ShouldNotBeNull("the node has an active profile");
        var profile = (await client.GetProfileAsync(active.ProfileId, ct)).Value.ShouldNotBeNull();
        profile.Equipment.OTAs.ShouldNotBeEmpty("the profile has an OTA");
        var camera = new Uri(profile.Equipment.OTAs[0].Camera);
        await NodeWait.UntilTheJobSucceedsAsync(client, await client.ConnectDeviceAsync(camera, ct), ct);
        try
        {
            // Each carrier in turn, twice, so neither is only ever measured first (P4b, #932).
            foreach (var sharedMemory in new[] { true, false, true, false })
            {
                await MeasureAsync(transport, client, roiWidth, roiHeight, sharedMemory, ct);
            }
        }
        finally
        {
            await NodeWait.UntilAsync("the capture to end", async token =>
            {
                var state = await client.GetPlanetaryAsync(token);
                return (state.Value is not { Running: true }, state.Value is { } s ? $"running, {s.FramesReceived} frames" : $"{state.StatusCode}");
            }, ct);
            await NodeWait.UntilTheJobSucceedsAsync(client, await client.DisconnectDeviceAsync(camera, skipWarmUp: true, ct), ct);
        }
    }

    private async Task MeasureAsync(NodeTransport transport, TianWenNodeClient client, int roiWidth, int roiHeight, bool sharedMemory,
        CancellationToken ct)
    {
        // A 1 ms exposure: the camera runs at its readout's rate, which is what a focus loop in daylight asks of it.
        var started = await client.StartPlanetaryAsync(new PlanetaryRequestDto { ExposureMs = 1, RoiWidth = roiWidth, RoiHeight = roiHeight }, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        try
        {
            await using var stream = await transport.OpenFrameStreamAsync(FrameSources.PlanetaryLive, sharedMemory, ct);
            var reader = new FrameReader();

            // Warm-up: the first frames pay for the camera's start, the node's first planes and the JIT.
            (int Width, int Height, int Channels, BitDepth Depth, bool Packed) shape = default;
            for (var i = 0; i < 10; i++)
            {
                var (_, warm) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull("the stream is open while the capture runs");
                shape = (warm.Width, warm.Height, warm.ChannelCount, warm.BitDepth, FrameWire.PacksAsUInt16(warm));
                warm.Release();
            }

            // The stream: ask, wait for the node's next frame, read it.
            var reads = new List<double>(Frames);
            var numbers = new List<int>(Frames);
            var before = (await client.GetPlanetaryAsync(ct)).Value;
            var whole = Stopwatch.StartNew();
            for (var i = 0; i < Frames; i++)
            {
                var one = Stopwatch.StartNew();
                var (number, frame) = (await stream.ReadAsync(reader, ct)).ShouldNotBeNull();
                reads.Add(one.Elapsed.TotalMilliseconds);
                numbers.Add(number);
                frame.Release();
            }
            whole.Stop();
            var after = (await client.GetPlanetaryAsync(ct)).Value;

            // One frame's carry alone: a fetch of the frame on show, which is already there.
            var carries = new List<double>(30);
            for (var i = 0; i < 30; i++)
            {
                var one = Stopwatch.StartNew();
                var fetched = await client.GetLatestFrameAsync(FrameSources.PlanetaryLive, after: null, reader, ct);
                carries.Add(one.Elapsed.TotalMilliseconds);
                fetched.Image?.Release();
            }

            var delivered = before is { } b && after is { } a ? (a.FramesReceived - b.FramesReceived) / whole.Elapsed.TotalSeconds : double.NaN;
            var published = numbers.Count > 1 ? (numbers[^1] - numbers[0]) / whole.Elapsed.TotalSeconds : double.NaN;
            var bytesPerSample = shape.Packed ? 2 : 4;
            var megabytes = shape.Width * (double)shape.Height * shape.Channels * bytesPerSample / 1e6;

            output.WriteLine($"=== {(sharedMemory ? "shared memory" : "socket")}: {stream.FramesFromSharedMemory} frames from slots, {stream.TornFrames} torn");
            output.WriteLine($"ROI asked {roiWidth}x{roiHeight}, got {shape.Width}x{shape.Height}x{shape.Channels} {shape.Depth}, "
                + $"{(shape.Packed ? "packed to 16 bits" : "float")}, about {megabytes:F1} MB a frame on the wire");
            output.WriteLine($"camera        {delivered,7:F1} fps (the node's own count; it reports {after?.FramesPerSecond:F1})");
            output.WriteLine($"node publish  {published,7:F1} fps (its live sample, at most one per 33 ms)");
            output.WriteLine($"client stream {Frames / whole.Elapsed.TotalSeconds,7:F1} fps, a read {Percentiles(reads)}");
            output.WriteLine($"one carry     {Percentiles(carries)} (a frame already there, fetched)");
            output.WriteLine($"skipped by the stream: {numbers.Zip(numbers.Skip(1), (x, y) => y - x - 1).Sum()} of the node's samples");
        }
        finally
        {
            await client.StopPlanetaryAsync(ct);
        }
    }

    private static string Percentiles(List<double> ms)
    {
        var sorted = ms.Order().ToArray();
        double At(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Round(p * (sorted.Length - 1)))];
        return $"p50 {At(0.5):F1} ms, p90 {At(0.9):F1} ms, max {sorted[^1]:F1} ms";
    }
}
