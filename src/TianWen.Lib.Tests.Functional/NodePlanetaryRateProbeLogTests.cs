using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The node rate probe reads its node's camera streams back from the node's log (<see cref="NodePlanetaryRateProbe.ReadStreams"/>),
/// and a pan's and a gain's lines are written by the thread pool (review round 1 of #1621), so each lands late and can land past
/// its stream's end line, or past the next stream's start. Each carries the time the stream took it (#1628), and the probe places
/// and times it by that, never by when or where it was written. The log is written by the node's own <see cref="FileLoggerProvider"/>,
/// in the DAL's wording.
/// </summary>
public sealed class NodePlanetaryRateProbeLogTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void APanAndAGainAreReadAtTheTimeTheStreamTookThemWhereverTheirLinesLanded()
    {
        var dataRoot = _folders.Create("rateprobe").FullName;
        // UTC, as the DAL's clock says it; the log's own times are local, at the node's UTC+11.
        var t0 = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(t0);
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+11", TimeSpan.FromHours(11), "UTC+11", "UTC+11"));
        using (var provider = new FileLoggerProvider("Server", Path.Combine(dataRoot, "Logs"), clock))
        {
            var dal = provider.CreateLogger("ToupTekDevice");
            void At(double seconds) => clock.SetUtcNow(t0.AddSeconds(seconds));

            At(0);
            dal.LogInformation("{Camera} streaming video: {Width}x{Height} at ({X}, {Y}), {Depth}, {Exposure} ms", "G3M678M", 640, 480, 2052, 1324, BitDepth.Int8, 5.0);
            // In order, as a quiet pool writes it.
            At(3.2);
            dal.LogDebug("{Camera}'s stream window is {Width}x{Height} at ({X}, {Y}) after a pan, as of {At:O}", "G3M678M", 640, 480, 1716, 546, t0.AddSeconds(3.1));
            At(70);
            dal.LogInformation("{Camera} streamed 13016 frames in 70.0 s (185.9 a second): the reader took 13000, 16 were dropped by the full hand-off (at most 64 waited), "
                + "the body dropped 6, 0 stalls, 0 recovered", "G3M678M");
            // A gain the stream took at 20 s, written by a pool a busy machine held, after its stream's end line.
            At(70.4);
            dal.LogDebug("{Camera}'s stream is at gain {Gain} as of {At:O}", "G3M678M", 2253, t0.AddSeconds(20));
            At(80);
            dal.LogInformation("{Camera} streaming video: {Width}x{Height} at ({X}, {Y}), {Depth}, {Exposure} ms", "G3M678M", 640, 480, 2052, 1324, BitDepth.Int8, 5.0);
            // The first stream's last gain and pan, written later still: after the next stream started.
            At(80.6);
            dal.LogDebug("{Camera}'s stream is at gain {Gain} as of {At:O}", "G3M678M", 2252, t0.AddSeconds(40));
            dal.LogDebug("{Camera}'s stream window is {Width}x{Height} at ({X}, {Y}) after a pan, as of {At:O}", "G3M678M", 640, 480, 1720, 546, t0.AddSeconds(41));
            At(90.1);
            dal.LogDebug("{Camera}'s stream is at gain {Gain} as of {At:O}", "G3M678M", 2253, t0.AddSeconds(90));
            At(150);
            dal.LogInformation("{Camera} streamed 12000 frames in 70.0 s (171.4 a second): the reader took 12000, 0 were dropped by the full hand-off (at most 12 waited), "
                + "the body dropped 6, 0 stalls, 0 recovered", "G3M678M");
        }

        var streams = NodePlanetaryRateProbe.ReadStreams(dataRoot);

        streams.Count.ShouldBe(2);
        streams[0].Gains.ShouldNotBeNull().ShouldBe([(t0.AddSeconds(20), 2253), (t0.AddSeconds(40), 2252)],
            "the first stream's gains, at the times the stream took them: one written after its end, one after the next stream's start");
        streams[0].Panned.ShouldBe(new RoiRect(1720, 546, 640, 480), "the first stream's last pan, though its line came after the next stream started");
        streams[1].Gains.ShouldNotBeNull().ShouldBe([(t0.AddSeconds(90), 2253)], "the second stream has only its own gain");
        streams[1].Panned.ShouldBeNull("the second stream never panned");
        streams.Select(static s => s.Replaced).ShouldBe([16L, 0L]);
    }
}
