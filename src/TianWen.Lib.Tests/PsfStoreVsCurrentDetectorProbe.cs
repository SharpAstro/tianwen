using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reports whether a dataset's stored PSF measurements still describe what the CURRENT star detector
/// measures on the same masters, so "does a detection change invalidate the store?" is answered with a
/// number instead of an argument.
/// </summary>
/// <remarks>
/// <para>Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir (the one holding
/// <c>stats/psf-sessions.jsonl</c> and <c>session-masters/</c>); it reads a real archive and is far too
/// slow for the ordinary suite.</para>
///
/// <para><b>It compares like with like as far as it can, and says where it cannot.</b> The stored
/// <c>BinsByChannel[c][r].Fwhm</c> arrays are the stars the report sampled: banded to a flux percentile
/// window and split by field radius. This probe pools them and compares against a pooled fresh
/// detection at the same <c>snrMin</c>/<c>maxStars</c>, so the STAR SELECTION differs even if the
/// detector had not changed at all. That makes the pooled median a lower bound on the disagreement, not
/// an unbiased estimate of it -- which is the safe direction for deciding whether to re-measure, and
/// the reason the count is reported beside it.</para>
///
/// <para>A re-measure is <c>tianwen dataset build --force-psf</c>, which replaces existing records
/// (<c>--regen-psf</c> only FILLS GAPS and cannot correct a record that is present and wrong).</para>
/// </remarks>
public class PsfStoreVsCurrentDetectorProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const float SnrMin = 5f;
    private const int MaxStars = 3000;

    private static float Pct(List<float> sorted, double p)
        => sorted.Count == 0 ? float.NaN : sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];

    [Fact]
    public async Task ReportWhetherTheStoredPsfStillMatchesTheCurrentDetector()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");

        var storePath = Path.Combine(root!, "stats", "psf-sessions.jsonl");
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(File.Exists(storePath), $"no PSF store at {storePath}");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        // Last record wins per session id, matching JsonLinesFile.ReadLastPerKeyAsync.
        var stored = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(storePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var rootEl = doc.RootElement;
            if (!rootEl.TryGetProperty("SessionId", out var idEl)
                || !rootEl.TryGetProperty("BinsByChannel", out var binsEl))
            {
                continue;
            }

            var pooled = new List<float>();
            foreach (var channel in binsEl.EnumerateArray())
            {
                foreach (var bin in channel.EnumerateArray())
                {
                    if (bin.TryGetProperty("Fwhm", out var fwhmEl))
                    {
                        foreach (var v in fwhmEl.EnumerateArray())
                        {
                            var f = v.GetSingle();
                            if (float.IsFinite(f) && f > 0)
                            {
                                pooled.Add(f);
                            }
                        }
                    }
                }
            }

            stored[idEl.GetString() ?? ""] = pooled;
        }

        output.WriteLine($"store    {stored.Count} sessions with pooled bins, from {Path.GetFileName(storePath)}");
        output.WriteLine($"store    last written {File.GetLastWriteTimeUtc(storePath):u}");
        output.WriteLine("");

        // Capped by default: this reads whole float masters off a spinning archive disk, and a
        // dozen sessions already answers a distributional question. TIANWEN_PSF_PROBE_MAX=0 for all.
        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_PSF_PROBE_MAX"), out var m) ? m : 8;
        var masters = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f).ToArray();
        if (maxMasters > 0 && masters.Length > maxMasters)
        {
            masters = [.. masters.Take(maxMasters)];
        }
        output.WriteLine($"masters  {masters.Length} retained");
        output.WriteLine("");
        output.WriteLine($"{"master",-46} {"stored n",8} {"p50",6} | {"fresh n",8} {"p50",6} {"p05",6} {"p95",6} | {"dp50",7}");

        var ct = TestContext.Current.CancellationToken;
        var deltas = new List<float>();
        var countRatios = new List<float>();

        foreach (var master in masters)
        {
            if (!Image.TryReadFitsFile(master, out var image) || image is null)
            {
                output.WriteLine($"{Path.GetFileNameWithoutExtension(master)[..Math.Min(46, Path.GetFileNameWithoutExtension(master).Length)],-46} (unreadable)");
                continue;
            }

            var fresh = new List<float>();
            var channels = image.Shape.ChannelCount;
            for (var c = 0; c < channels; c++)
            {
                var stars = await image.FindStarsAsync(channel: c, snrMin: SnrMin, maxStars: MaxStars, cancellationToken: ct);
                foreach (var s in stars)
                {
                    if (float.IsFinite(s.StarFWHM) && s.StarFWHM > 0)
                    {
                        fresh.Add(s.StarFWHM);
                    }
                }
            }
            image.Release();

            fresh.Sort();

            // Session id in the store is "<camera-dir>/<filter>/<target>/<night>|<camera>|"; the master
            // file name is those same parts underscore-joined, so match on the leading path segments.
            var name = Path.GetFileNameWithoutExtension(master);
            var match = stored.FirstOrDefault(kv =>
                name.StartsWith(string.Join('_', kv.Key.Split('|')[0].Split('/')), StringComparison.Ordinal));

            var storedPooled = match.Value;
            if (storedPooled is { Count: > 0 })
            {
                storedPooled.Sort();
            }

            var storedP50 = storedPooled is { Count: > 0 } ? Pct(storedPooled, 0.50) : float.NaN;
            var freshP50 = Pct(fresh, 0.50);
            var delta = storedP50 - freshP50;

            output.WriteLine(
                $"{name[..Math.Min(46, name.Length)],-46} {storedPooled?.Count ?? 0,8} {storedP50,6:F3} | " +
                $"{fresh.Count,8} {freshP50,6:F3} {Pct(fresh, 0.05),6:F3} {Pct(fresh, 0.95),6:F3} | {delta,7:F3}");

            if (float.IsFinite(delta) && storedPooled is { Count: > 0 })
            {
                deltas.Add(delta);
                countRatios.Add((float)fresh.Count / storedPooled.Count);
            }
        }

        output.WriteLine("");
        if (deltas.Count > 0)
        {
            deltas.Sort();
            countRatios.Sort();
            output.WriteLine($"matched  {deltas.Count} sessions");
            output.WriteLine($"dp50     stored-minus-fresh median FWHM: p05={Pct(deltas, 0.05):F3} p50={Pct(deltas, 0.50):F3} p95={Pct(deltas, 0.95):F3} px");
            output.WriteLine($"count    fresh/stored star count ratio:  p05={Pct(countRatios, 0.05):F3} p50={Pct(countRatios, 0.50):F3} p95={Pct(countRatios, 0.95):F3}");
            output.WriteLine("");
            output.WriteLine("A dp50 near zero means the store still describes the current detector on the pooled");
            output.WriteLine("median. It does NOT clear the close-pair population, which is a small tail by count");
            output.WriteLine("and cannot move a median: judge that on the count ratio and on the low percentiles.");
        }
        else
        {
            output.WriteLine("no session matched a stored record -- check the id-to-filename mapping above");
        }
    }
}
