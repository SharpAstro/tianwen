using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: a saturated star's brightness from outside the image. Each master's catalogued stars (Moffat-subtracted, every
/// channel's amplitude) are matched to Tycho-2 through the master's own WCS by SPCC's matcher
/// (<see cref="Tycho2ColorCalibration.MatchStars"/>, brightest first, each catalogue star claimed once, proper motion carried
/// to the epoch), its magnitude gate opened wide so a saturated star whose amplitude is in question is not gated by it. Writes
/// one row per match: position, significance, saturated or not, the mean of the channel amplitudes (the profile is the field's
/// for every star, so the amplitude is the flux up to one constant), V and B-V. Unsaturated stars calibrate amplitude against
/// magnitude; a saturated star's own magnitude then says which amplitude is its true one.
/// <para>Set <c>TIANWEN_SATEDGE_BAKE</c>, <c>TIANWEN_SATEDGE_PLATES</c>, <c>TIANWEN_SATEDGE_OUT</c> and
/// <c>TIANWEN_SATEDGE_SESSIONS</c> as for <see cref="SaturatedEdgeProbe"/>.</para>
/// </summary>
[Collection("Imaging")]
public sealed class SaturatedPhotometryProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task MatchEachMastersCataloguedStarsToTycho2()
    {
        var bake = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_BAKE");
        var platesRoot = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_PLATES");
        var outDir = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_OUT");
        var sessions = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_SESSIONS");
        Assert.SkipWhen(bake is null || platesRoot is null || outDir is null || sessions is null, "TIANWEN_SATEDGE_* not set");
        var ct = TestContext.Current.CancellationToken;
        var psfStore = await DatasetPsfStore.ReadAsync(Path.Combine(bake, "stats", DatasetPsfStore.FileName), null, ct);
        var context = new DatasetDegradationExporter.StarsContext(Path.Combine(platesRoot, "plates"), psfStore);
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        Directory.CreateDirectory(outDir);

        foreach (var prefix in sessions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sessionId = psfStore.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && File.Exists(RetainedMasterStore.PathFor(bake, k)))
                .Order(StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException($"no session with a retained master in the PSF store starts with {prefix}");
            var masterPath = RetainedMasterStore.PathFor(bake, sessionId);
            Assert.True(Image.TryReadFitsFile(masterPath, out var master, out var wcs), $"{sessionId}: unreadable master");
            var slug = DatasetTileExporter.Sanitize(sessionId);
            if (wcs is not { } solved)
            {
                output.WriteLine($"{slug}: no WCS, skipped");
                master.Release();
                continue;
            }
            var catalogue = await StarlessCatalogue.ReadAsync(context.CataloguePath(sessionId), ct);
            var (channels, width, height) = master.Shape;
            var stars = catalogue.Where(s => s.Outcome == StarFitOutcome.Subtracted && s.Model == StarFitModel.Moffat
                    && s.ChannelAmplitudes.Length == channels && s.ChannelAmplitudes.All(static a => float.IsFinite(a) && a > 0))
                .ToArray();
            // The matcher keys a detection by its position and orders by flux; the amplitude is the flux up to the field
            // profile's integral, the same for every star.
            var byPosition = stars.GroupBy(static s => (s.X, s.Y)).ToDictionary(static g => g.Key, static g => g.First());
            var list = new StarList(new ConcurrentBag<ImagedStar>(stars.Select(static s =>
                new ImagedStar(0f, 0f, s.Significance, s.ChannelAmplitudes.Average(), s.X, s.Y, 0f))));
            var (matches, funnel) = Tycho2ColorCalibration.MatchStars(list, solved, db, matchRadiusArcsec: 5f, maxMagDiff: 10f,
                Tycho2ColorCalibration.ComputeDtJulianYears(master), width, height);

            var csv = new StringBuilder("x,y,significance,saturated,amplitude,v,bv\n");
            foreach (var (star, tycho) in matches)
            {
                if (!byPosition.TryGetValue((star.XCentroid, star.YCentroid), out var s) || Half.IsNaN(tycho.V_Mag))
                {
                    continue;
                }
                csv.Append(CultureInfo.InvariantCulture,
                    $"{s.X:F2},{s.Y:F2},{s.Significance:F0},{(s.Saturated ? 1 : 0)},{star.Flux:G6},{(float)tycho.V_Mag:F3},{(float)tycho.BMinusV:F3}\n");
            }
            await File.WriteAllTextAsync(Path.Combine(outDir, slug + ".phot.csv"), csv.ToString(), ct);
            output.WriteLine($"{slug}: {stars.Length} catalogued stars, {matches.Count} matched to Tycho-2 ({matches.Count(m => byPosition.TryGetValue((m.Star.XCentroid, m.Star.YCentroid), out var s) && s.Saturated)} saturated)");
            master.Release();
        }
    }
}
