using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: why does the quad match miss a sub that plate-solves at its neighbours' pointing? Reruns
/// <see cref="FrameRegistration.TryMatchAsync"/> for given subs against the session's reference and against a
/// temporal neighbour, at several top-K sizes, on the raw frames.
///
/// <para>Set <c>TIANWEN_MATCH_DIR</c> to the lights folder, <c>TIANWEN_MATCH_REF</c> to a substring naming the
/// reference sub, and <c>TIANWEN_MATCH_PAIRS</c> to <c>sub:neighbour</c> substrings separated by semicolons (found
/// on the SV605CC Sculptor of 2026-09-12: 0039 to 0047 missed against 0086, taken after the meridian flip).</para>
/// </summary>
public sealed class SubMatchProbe(ITestOutputHelper output)
{
    [Fact(Timeout = 1_800_000)]
    public async Task MatchSubsAgainstTheReferenceAndANeighbour()
    {
        var dir = Environment.GetEnvironmentVariable("TIANWEN_MATCH_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "TIANWEN_MATCH_DIR not set");
        var ct = TestContext.Current.CancellationToken;
        var files = Directory.GetFiles(dir!, "*.fits");
        string Find(string key) => files.Single(f => Path.GetFileName(f).Contains(key, StringComparison.Ordinal));

        // The stack detects on CALIBRATED subs: TIANWEN_MATCH_DARK / TIANWEN_MATCH_FLAT name its masters.
        Image? Master(string variable)
            => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } p && Image.TryReadFitsFile(p, out var m) ? m : null;
        var calibrator = new Calibrator(Bias: null, Dark: Master("TIANWEN_MATCH_DARK"), Flat: Master("TIANWEN_MATCH_FLAT"), Pedestal: 0f);

        async Task<StarList> Detect(string path)
        {
            Image.TryReadFitsFile(path, out var raw).ShouldBeTrue(path);
            var calibrated = calibrator.Apply(raw);
            var (stars, debayered) = await FrameRegistration.DetectAsync(calibrated, DebayerAlgorithm.VNG, snrMin: 5f, minStars: 2000, cancellationToken: ct);
            debayered.Release();
            return stars;
        }

        var reference = await Detect(Find(Environment.GetEnvironmentVariable("TIANWEN_MATCH_REF") ?? ""));
        foreach (var pair in (Environment.GetEnvironmentVariable("TIANWEN_MATCH_PAIRS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var keys = pair.Split(':');
            var sub = await Detect(Find(keys[0]));
            var neighbour = await Detect(Find(keys[1]));
            foreach (var k in new[] { 50, 100, 150, 200, 300 })
            {
                output.WriteLine($"{keys[0]} (stars {sub.Count}) k={k,3}: vs reference {await Fit(sub, reference, k),-28} vs {keys[1]} {await Fit(sub, neighbour, k)}");
            }
        }

        static async Task<string> Fit(StarList a, StarList b, int k)
        {
            using var sa = new SortedStarList(a);
            using var sb = new SortedStarList(b);
            var (solution, tolerance, rms) = await FrameRegistration.TryMatchAsync(sa, sb, k);
            if (solution is { } m)
            {
                // What the stack does next: the rigid refinement over every star.
                var (_, scale, rot, _, _, refineRms, pairs, _) = RegistrationRefiner.RefineRigid(sa, sb, m);
                return $"qt={tolerance:0.###} rms={rms:F2} rot={MathF.Atan2(m.M12, m.M11) * 180f / MathF.PI:F1} refine rms={refineRms:F2} pairs={pairs}";
            }
            // Why not: at each rung, how many quad pairs, how many RANSAC kept, and the refit's scale ratio and skew
            // against the 1e-3 the acceptance demands.
            var why = new System.Text.StringBuilder("NO FIT");
            foreach (var qt in FrameRegistration.QuadTolerances)
            {
                var (table, d) = StarReferenceTable.FindFitWithDiagnostics(await sa.FindQuadsAsync(k), await sb.FindQuadsAsync(k), 6, qt);
                why.Append($" | {qt:0.###}: pairs {d.RawPairs} kept {d.FilteredPairs}");
                if (table?.FitAffineTransform() is { } fit)
                {
                    var (scale, skew, _, _) = fit.Decompose();
                    why.Append($" scale {scale.X / scale.Y - 1f:+0.0000;-0.0000} skew {skew.X:+0.0000;-0.0000},{skew.Y:+0.0000;-0.0000} affine rms {table.ComputeRmsResidualPx(fit):F2}");
                }
                if (table?.FitSimilarityTransform() is { } sim)
                {
                    why.Append($" similarity rms {table.ComputeRmsResidualPx(sim):F2}");
                }
            }
            return why.ToString();
        }
    }
}
