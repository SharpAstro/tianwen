using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// On demand (A4, #1391): every real capture under the roots <c>TIANWEN_IDENTIFY_PROBE</c> names (separated by <c>;</c>), each with
/// its planet's median elongation over the frames (<see cref="PlanetaryIdentification.ElongationAsync"/>), Saturn's ring opening at its
/// middle and what AUTO identifies it as: the reading the ring test's two constants were set from, by the rule set on #1391 first.
/// Read-only; written as tab-separated lines to the test's output.
/// </summary>
public class PlanetaryIdentificationProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_IDENTIFY_PROBE";

    [Fact(Timeout = 3_600_000)]
    public async Task EveryCaptureOnHandWithItsElongationAndRingOpening()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");
        var ct = TestContext.Current.CancellationToken;
        var roots = (Environment.GetEnvironmentVariable(EnvVar) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine("path\tlayout\tframes\tpath planet\telongation\topening deg\tidentified");
        foreach (var capture in roots.Where(Directory.Exists)
            .SelectMany(root => FileEnumeration.EnumerateFiles(root, ".ser", recursive: true))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = SerFrameStream.Open(capture);
                var elongation = await PlanetaryIdentification.ElongationAsync(stream, ct);
                var middle = stream.CaptureSpan is { } span ? span.Earliest + ((span.Latest - span.Earliest) / 2) : (DateTimeOffset?)null;
                var opening = middle is { } at ? PlanetaryIdentification.RingOpeningDeg(at) : double.NaN;
                var identity = await PlanetaryIdentification.IdentifyAsync(capture, cancellationToken: ct);
                output.WriteLine(string.Create(inv,
                    $"{capture}\t{stream.Layout}\t{stream.FrameCount}\t{PlanetaryCaptureName.Planet(capture)}\t{elongation:0.000}\t{opening:0.000}\t{identity.Describe()}"));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                output.WriteLine($"{capture}\tunreadable: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
