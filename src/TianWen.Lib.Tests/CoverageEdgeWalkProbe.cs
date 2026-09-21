using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the edge walk actually sees on a REAL master, swept across the trim fraction. A probe, not
/// an assertion: the answer comes from the master rather than from reasoning about it.
///
/// <para>The question it was written for: <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/> was
/// read twice in <c>Trim</c>, once as the cap on what an edge may lose and once as the depth the
/// profile must have SETTLED within, so a dither strip deeper than the cap was refused outright and
/// sweeping the one knob moved both meanings together. The split has since landed, and the walk now
/// reports the settle depth directly (<see cref="CoverageEdgeTrim.SettleDepth"/>), so the sweep's job
/// changed: it shows that the cap, and only the cap, moves an edge between
/// <see cref="CoverageEdgeOutcome.BeyondCap"/> and <see cref="CoverageEdgeOutcome.Trimmed"/>, at the
/// depth the shipped run already named. An earlier version of this file keyed on the <c>Settled</c>
/// bool, which after the split is true for a band past the cap, and so answered the first swept value
/// for every edge.</para>
///
/// <para>Env-gated on <c>TIANWEN_CROP_PROBE</c> (a FITS master, or a directory of them) so a bare
/// <c>dotnet test</c> skips it: the files live outside the repo and are gigabytes of someone's
/// archive. Optional <c>TIANWEN_CROP_PROBE_LIMIT</c> caps how many a directory contributes.</para>
/// </summary>
public class CoverageEdgeWalkProbe(ITestOutputHelper output)
{
    /// <summary>The sweep. Spans the shipped 0.05 and the 10.2% strip that motivated the split.</summary>
    private static readonly double[] Fractions = [0.02, 0.05, 0.08, 0.10, 0.12, 0.15, 0.20, 0.25];

    [Fact]
    public void ReportWhatTheEdgeWalkSeesAcrossTheTrimFraction()
    {
        var target = Environment.GetEnvironmentVariable("TIANWEN_CROP_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(target) || (!File.Exists(target) && !Directory.Exists(target)),
            "set TIANWEN_CROP_PROBE to a FITS master or a directory of them");

        var limit = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_CROP_PROBE_LIMIT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 12;

        var files = File.Exists(target) ? [target!] : Masters(target!, recursive: false).Take(limit).ToArray();

        output.WriteLine($"{files.Length} master(s)");

        foreach (var file in files)
        {
            if (!Image.TryReadFitsFile(file, out var image) || image is null)
            {
                output.WriteLine($"\n{Path.GetFileName(file)}: could not read");
                continue;
            }

            // The covered rectangle, not the frame: see the corpus fact below for what the frame does
            // to an edge that still has its canvas ring, and how many edges it did it to.
            var full = image.LargestCoveredRectangle();
            output.WriteLine($"\n=== {Path.GetFileName(file)}  frame {image.Width}x{image.Height}"
                + $"  covered {full.Width}x{full.Height} at ({full.X},{full.Y}) ===");

            if (full.Width <= 0 || full.Height <= 0)
            {
                output.WriteLine("  no covered rectangle; there is nothing the walk could be asked");
                image.Release();
                continue;
            }

            // The shipped verdict first, so the sweep below is read against what ships today.
            var shipped = CoverageEdgeWalk.Measure(image, full);
            output.WriteLine($"  shipped (MaxTrimFraction {CoverageEdgeWalkOptions.Default.MaxTrimFraction:F2}): "
                + Describe(shipped) + (shipped.AnyDeclined ? "   <- AT LEAST ONE EDGE DECLINED" : ""));

            output.WriteLine($"  {"frac",6} {"cap px",7} | {"left",-16} {"top",-16} {"right",-16} {"bottom",-16}");
            foreach (var f in Fractions)
            {
                var o = CoverageEdgeWalkOptions.Default with { MaxTrimFraction = f };
                var t = CoverageEdgeWalk.Measure(image, full, o);
                var capX = (int)(f * full.Width);
                output.WriteLine($"  {f,6:F2} {capX,7} | {Cell(t.Left)} {Cell(t.Top)} {Cell(t.Right)} {Cell(t.Bottom)}");
            }

            // The settle depth per edge, as the walk itself now reports it, beside the shallowest swept
            // cap at which the edge comes off. The two should agree: the cap has to reach the depth.
            output.WriteLine("  settles at: " + string.Join("  ", new[]
            {
                ("left", shipped.Left, TrimsAtFraction(image, full, static t => t.Left)),
                ("top", shipped.Top, TrimsAtFraction(image, full, static t => t.Top)),
                ("right", shipped.Right, TrimsAtFraction(image, full, static t => t.Right)),
                ("bottom", shipped.Bottom, TrimsAtFraction(image, full, static t => t.Bottom)),
            }.Select(static p => $"{p.Item1} {(p.Item2.SettleDepth >= 0 ? p.Item2.SettleDepth + "px" : "never")}"
                + $" (trims from cap {(p.Item3 is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "none swept")})")));

            image.Release();
        }
    }

    private static string Cell(CoverageEdgeTrim t)
        => (t.Outcome switch
        {
            CoverageEdgeOutcome.Trimmed => $"{t.Depth}px r={t.EdgeRatio:F2}",
            CoverageEdgeOutcome.Clean => $"clean r={t.EdgeRatio:F2}",
            CoverageEdgeOutcome.BeyondCap => $"CAP<{t.SettleDepth}px",
            CoverageEdgeOutcome.NeverSettles => $"NEVER r={t.EdgeRatio:F2}",
            _ => "unmeasurable",
        }).PadRight(16);

    private static string Describe(CoverageEdgeTrims t)
        => $"L {Cell(t.Left).Trim()}  T {Cell(t.Top).Trim()}  R {Cell(t.Right).Trim()}  B {Cell(t.Bottom).Trim()}";

    /// <summary>The integrated masters under a root: every <c>.fits</c> that is not one of our own
    /// sidecars. Through <see cref="FileEnumeration"/> rather than a <see cref="SearchOption"/> overload,
    /// which is the repository rule and matters here: a bake store is reached through junctions.</summary>
    private static IEnumerable<string> Masters(string root, bool recursive)
        => FileEnumeration.EnumerateFiles(root, ".fits", recursive)
            .Where(static f => !f.Contains(".rejection.", StringComparison.OrdinalIgnoreCase)
                            && !f.Contains(".coverage.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>The sweep over how far the walk LOOKS, which is the question the split left open. The cap
    /// only decides whether a depth already found is paid for; the SEARCH decides whether it is found at
    /// all, and a profile that "settles" at the end of its window may be settling on the window rather
    /// than on the frame. The widest value is near the practical ceiling: the reference pool starts at
    /// <c>2 * search + BandThickness</c>, so past about 0.45 nothing is measurable at all.</summary>
    private static readonly double[] DefaultSearches = [0.05, 0.075, 0.10, 0.15, 0.20, 0.25, 0.30];

    /// <summary>The sweep, or just the windows named in <c>TIANWEN_CROP_CORPUS_WINDOWS</c>. Naming one
    /// is how the mix AT THE SHIPPED DEFAULTS is checked after a change to the walk, at a seventh of
    /// the cost of re-sweeping.</summary>
    private static double[] Searches
        => Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_WINDOWS") is { Length: > 0 } list
            ? list.Split(',').Select(static s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray()
            : DefaultSearches;

    /// <summary>
    /// Where every edge of a whole corpus settles, as the search window widens. This is the measurement
    /// the default should come from: one row per (master, edge, search), so an edge whose depth STOPS
    /// MOVING as the window grows can be told from one the window is merely chasing.
    /// </summary>
    /// <remarks>
    /// Env-gated on <c>TIANWEN_CROP_CORPUS</c> (a directory, walked recursively), with
    /// <c>TIANWEN_CROP_CORPUS_LIMIT</c> to cap it and <c>TIANWEN_CROP_CORPUS_CSV</c> to write the rows.
    /// Each master is read ONCE and swept in memory: the corpus is tens of gigabytes on a spindle, so
    /// re-reading per search fraction would make the I/O the measurement.
    /// <para><c>TIANWEN_CROP_CORPUS_REPLAY=&lt;a csv this wrote earlier&gt;</c> re-runs the SUMMARY over
    /// rows already measured, reading no FITS at all. The corpus run is 25 minutes on this spindle and
    /// the classification is the part that gets revised, so it reads its own output back rather than
    /// growing a second implementation in a script, which could then disagree with this one.</para>
    /// </remarks>
    [Fact]
    public void ReportWhereEveryEdgeOfACorpusSettlesAsTheWindowWidens()
    {
        var replay = Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_REPLAY");
        if (replay is { Length: > 0 } && File.Exists(replay))
        {
            var replayed = ReadRows(replay);
            output.WriteLine($"replaying {replayed.Count} rows from {replay}");
            SummariseCorpus(replayed, output);
            if (Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_COMPARE") is { Length: > 0 } baseline
                && File.Exists(baseline))
            {
                CompareToBaseline(replayed, baseline, output);
            }

            return;
        }

        var root = Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root) || !Directory.Exists(root),
            "set TIANWEN_CROP_CORPUS to a directory of FITS masters, or TIANWEN_CROP_CORPUS_REPLAY to a csv");

        var limit = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_LIMIT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
        var csvPath = Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_CSV");

        var files = Masters(root!, recursive: true).Take(limit).ToArray();
        output.WriteLine($"{files.Length} master(s) under {root}");

        var csv = new StringBuilder("file,edge,width,height,span,search,settleDepth,settleFrac,outcome,edgeRatio\n");
        var rows = new List<(string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)>();
        var read = 0;

        foreach (var file in files)
        {
            if (!Image.TryReadFitsFile(file, out var image) || image is null)
            {
                output.WriteLine($"{Path.GetFileName(file)}: could not read");
                continue;
            }

            read++;
            var name = Path.GetFileNameWithoutExtension(file);

            // Start where the PRODUCTION callers start. All three of them (ViewerActions.ScanForCrop,
            // Image.SettledCoverageRectangle, ClassicalBackgroundExtractor) hand the walk the covered
            // rectangle, never the frame. The walk profiles an UNDER-EXPOSED band, and a canvas ring is
            // not one: fed the whole frame, a master that still has its ring has its outermost band
            // INSIDE that ring, where every pixel holds the same value, so the difference sigma is 0,
            // the ratio is 0.00 and the edge reads "clean". Measured on the first run of this probe:
            // 43 edges over 17 of the 139 masters, every one of them a false Clean, and none of them
            // describing anything that ships.
            var union = image.LargestCoveredRectangle();
            if (union.Width <= 0 || union.Height <= 0)
            {
                output.WriteLine($"{name}: no covered rectangle");
                image.Release();
                continue;
            }

            foreach (var search in Searches)
            {
                var options = CoverageEdgeWalkOptions.Default with { SettleSearchFraction = search };
                if (Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_CONFIRM") is { Length: > 0 } cf)
                {
                    options = options with { ConfirmFactor = double.Parse(cf, CultureInfo.InvariantCulture) };
                }

                var t = CoverageEdgeWalk.Measure(image, union, options);

                // The span is the COVERED rectangle's, not the frame's, because that is what every
                // depth here is a depth into and what the walk's own fractions are fractions of.
                foreach (var (edge, trim, span) in new[]
                {
                    ("left", t.Left, union.Width),
                    ("right", t.Right, union.Width),
                    ("top", t.Top, union.Height),
                    ("bottom", t.Bottom, union.Height),
                })
                {
                    rows.Add((name, edge, span, search, trim.SettleDepth, trim.Outcome));
                    var frac = trim.SettleDepth >= 0 ? (double)trim.SettleDepth / span : double.NaN;
                    csv.Append(CultureInfo.InvariantCulture,
                        $"\"{name}\",{edge},{union.Width},{union.Height},{span},{search:F3},{trim.SettleDepth},{frac:F5},{trim.Outcome},{trim.EdgeRatio:F4}\n");
                }
            }

            image.Release();
        }

        output.WriteLine($"read {read} of {files.Length}");
        if (csvPath is { Length: > 0 })
        {
            File.WriteAllText(csvPath, csv.ToString());
            output.WriteLine($"rows -> {csvPath}");
        }

        SummariseCorpus(rows, output);
    }

    /// <summary>The corpus answer, printed rather than asserted: what each search window finds, and where
    /// the edges that DO settle sit as a fraction of their span. A default picked off this is picked off
    /// the frames; one picked off the first table above is picked off four of them.</summary>
    private static void SummariseCorpus(
        List<(string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)> rows,
        ITestOutputHelper output)
    {
        output.WriteLine("");
        output.WriteLine($"  {"search",7} {"clean",7} {"trimmed",8} {"beyond",7} {"never",7} {"unmeas",7} | settled depth as a fraction of span");
        foreach (var search in Searches)
        {
            var at = rows.Where(r => r.Search == search).ToArray();
            if (at.Length == 0)
            {
                continue;
            }

            var settledFracs = at.Where(static r => r.SettleDepth > 0)
                .Select(static r => (double)r.SettleDepth / r.Span)
                .OrderBy(static v => v)
                .ToArray();

            output.WriteLine($"  {search,7:F3} {Count(at, CoverageEdgeOutcome.Clean),7} {Count(at, CoverageEdgeOutcome.Trimmed),8} "
                + $"{Count(at, CoverageEdgeOutcome.BeyondCap),7} {Count(at, CoverageEdgeOutcome.NeverSettles),7} "
                + $"{Count(at, CoverageEdgeOutcome.NotMeasurable),7} | n={settledFracs.Length,4} "
                + $"p50 {Pct(settledFracs, 0.50):F4}  p90 {Pct(settledFracs, 0.90):F4}  p99 {Pct(settledFracs, 0.99):F4}  max {Pct(settledFracs, 1.0):F4}");
        }

        // The point of sweeping the window. An edge with a real border answers the same depth however far
        // the walk looks; an edge on a frame-wide gradient answers deeper every time the window grows,
        // because there is no border to find and "settled" only ever means "flat out to where I stopped".
        //
        // Compared ONLY over the windows that could have seen the border, which is the whole difficulty.
        // A window too narrow to reach a border does not report a truncated depth, it reports
        // NeverSettles (the outermost band is still above the margin, so the outside-in scan never
        // starts), so demanding a depth at EVERY window quietly throws out every border deeper than the
        // narrowest one and caps the surviving distribution at that window by construction. The first
        // run of this summary did exactly that and reported a maximum of 0.0490 against a 0.05 narrowest
        // window, which reads as a fact about the frames and is a fact about the filter.
        var byEdge = rows.GroupBy(static r => (r.File, r.Edge))
            .Select(static g => g.OrderBy(static r => r.Search).ToArray())
            .ToArray();

        var never = 0;
        var single = 0;
        var stable = new List<double>();
        var grows = new List<(string File, string Edge, int First, int Last)>();
        var deeperThanCap = new List<(string File, string Edge, int Depth, double Frac)>();
        foreach (var series in byEdge)
        {
            var depths = series.Where(static r => r.SettleDepth > 0).ToArray();
            if (depths.Length == 0)
            {
                never++;
                continue;
            }

            if (depths.Length == 1)
            {
                // One window saw something and no other did. Not enough to call a border, and counted
                // apart rather than folded in either direction.
                single++;
                continue;
            }

            var min = depths.Min(static r => r.SettleDepth);
            var max = depths.Max(static r => r.SettleDepth);
            if (max - min <= Math.Max(2 * CoverageEdgeWalkOptions.Default.Step, 0.10 * max))
            {
                var frac = (double)max / series[0].Span;
                stable.Add(frac);
                if (frac > CoverageEdgeWalkOptions.Default.MaxTrimFraction)
                {
                    deeperThanCap.Add((series[0].File, series[0].Edge, max, frac));
                }
            }
            else
            {
                grows.Add((series[0].File, series[0].Edge, depths[0].SettleDepth, depths[^1].SettleDepth));
            }
        }

        var sortedStable = stable.OrderBy(static v => v).ToArray();
        output.WriteLine("");
        output.WriteLine($"  {byEdge.Length} edges, classified over the windows that could SEE the border:");
        output.WriteLine($"    a border, same depth at every window that reached it : {stable.Count,5}");
        output.WriteLine($"    depth follows the window                             : {grows.Count,5}");
        output.WriteLine($"    one window only, too little to call                  : {single,5}");
        output.WriteLine($"    never settles at any window                          : {never,5}");
        output.WriteLine("");
        output.WriteLine("  a real border's depth as a fraction of its span (this is what the loss cap has to cover):");
        output.WriteLine($"    p50 {Pct(sortedStable, 0.50):F4}  p75 {Pct(sortedStable, 0.75):F4}  p90 {Pct(sortedStable, 0.90):F4}  "
            + $"p95 {Pct(sortedStable, 0.95):F4}  p99 {Pct(sortedStable, 0.99):F4}  max {Pct(sortedStable, 1.0):F4}");
        output.WriteLine($"  borders the shipped cap of {CoverageEdgeWalkOptions.Default.MaxTrimFraction:F2} does NOT reach: "
            + $"{deeperThanCap.Count} of {stable.Count}");
        foreach (var d in deeperThanCap.OrderByDescending(static d => d.Frac).Take(12))
        {
            output.WriteLine($"    past the cap: {d.Edge,-6} {d.Depth,5} px = {d.Frac:F4} of span  {d.File}");
        }

        foreach (var g in grows.OrderByDescending(static g => g.Last - g.First).Take(8))
        {
            output.WriteLine($"    follows the window: {g.Edge,-6} {g.First,5} -> {g.Last,-5} px  {g.File}");
        }

        // What the cap is actually FOR, which the distribution above cannot say on its own. At run time
        // the walk sees ONE window and cannot tell a border from a gradient that happens to go quiet
        // inside it; the sweep can, but only after the fact. So the cap is the only thing standing
        // between a gradient and a trim, and the question a default has to answer is what each setting
        // buys and what it costs AT THE SHIPPED WINDOW.
        var kind = Classify(rows);

        // Which window the cap analysis below is read at. The shipped one by default; naming another
        // answers "what would this cap buy if the window moved" off rows already measured, instead of
        // editing a shipped default to run an experiment and having to remember to put it back.
        var shippedSearch = Environment.GetEnvironmentVariable("TIANWEN_CROP_CORPUS_ANALYSE") is { Length: > 0 } w
            ? double.Parse(w, CultureInfo.InvariantCulture)
            : CoverageEdgeWalkOptions.Default.SettleSearchFraction;
        var atShipped = rows.Where(r => r.Search == shippedSearch && r.SettleDepth > 0).ToArray();
        output.WriteLine("");
        output.WriteLine($"  at the SHIPPED window of {shippedSearch:F2}, what each cap would trim ({atShipped.Length} edges report a depth):");
        output.WriteLine($"    {"cap",6} {"trims",6} {"of them borders",16} {"of them gradients",18}");
        foreach (var cap in new[] { 0.03, 0.05, 0.06, 0.08, 0.10, 0.12 })
        {
            var trimmed = atShipped.Where(r => (double)r.SettleDepth / r.Span <= cap).ToArray();
            var border = trimmed.Count(r => kind[(r.File, r.Edge)] == "border");
            var grow = trimmed.Count(r => kind[(r.File, r.Edge)] == "grows");
            output.WriteLine($"    {cap,6:F2} {trimmed.Length,6} {border,16} {grow,18}");
        }

        // Who the CLI's beyond-cap rule actually reaches. `--trim-declined` now takes such an edge down
        // to its measured SettleDepth instead of a flat fraction, which is right where there IS a border
        // and is a deeper bite where there is not, so the split between the two is the number that
        // judges the rule rather than describing it.
        var shippedCap = CoverageEdgeWalkOptions.Default.MaxTrimFraction;
        var beyond = atShipped.Where(r => (double)r.SettleDepth / r.Span > shippedCap).ToArray();
        var blind = (int)Math.Round(shippedCap * 100);
        output.WriteLine("");
        output.WriteLine($"  the {beyond.Length} edges beyond the {shippedCap:F2} cap at that window, which is what --trim-declined now trims to depth:");
        foreach (var group in beyond.GroupBy(r => kind[(r.File, r.Edge)]).OrderBy(static g => g.Key))
        {
            var fracs = group.Select(static r => (double)r.SettleDepth / r.Span).OrderBy(static v => v).ToArray();
            output.WriteLine($"    {group.Key,-8} {group.Count(),3}   depth/span p50 {Pct(fracs, 0.50):F4}  max {Pct(fracs, 1.0):F4}"
                + $"   (the blind fraction would have taken {blind}% of each)");
        }
    }

    /// <summary>Each edge's verdict over a whole window sweep: <c>border</c>, <c>grows</c>,
    /// <c>single</c> (one window saw a depth and no other did) or <c>never</c>. The one place the
    /// sweep is turned into a judgement, so a comparison between two runs cannot use a second rule.</summary>
    private static Dictionary<(string, string), string> Classify(
        List<(string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)> rows)
    {
        var kind = new Dictionary<(string, string), string>();
        foreach (var series in rows.GroupBy(static r => (r.File, r.Edge)))
        {
            var depths = series.Where(static r => r.SettleDepth > 0).ToArray();
            var key = (series.Key.File, series.Key.Edge);
            if (depths.Length == 0)
            {
                kind[key] = "never";
            }
            else if (depths.Length == 1)
            {
                kind[key] = "single";
            }
            else
            {
                var min = depths.Min(static r => r.SettleDepth);
                var max = depths.Max(static r => r.SettleDepth);
                kind[key] = max - min <= Math.Max(2 * CoverageEdgeWalkOptions.Default.Step, 0.10 * max) ? "border" : "grows";
            }
        }

        return kind;
    }

    /// <summary>
    /// What a change to the WALK did, judged against a sweep taken before it. The single-window run
    /// that checks a change cannot classify anything itself (one window per edge is one data point),
    /// so it is cross-tabulated against a baseline sweep's verdict: the question a contract change has
    /// to answer is not how the outcome mix moved but WHICH edges moved, and whether the ones that
    /// stopped being trimmed are the ones the sweep calls gradients.
    /// </summary>
    /// <remarks>Set <c>TIANWEN_CROP_CORPUS_COMPARE</c> to the baseline sweep's csv and
    /// <c>TIANWEN_CROP_CORPUS_REPLAY</c> to the new run's.</remarks>
    private static void CompareToBaseline(
        List<(string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)> now,
        string baselinePath,
        ITestOutputHelper output)
    {
        var baseline = Classify(ReadRows(baselinePath));
        var table = new Dictionary<(CoverageEdgeOutcome, string), int>();
        var missing = 0;
        foreach (var r in now)
        {
            if (!baseline.TryGetValue((r.File, r.Edge), out var was))
            {
                missing++;
                continue;
            }

            var key = (r.Outcome, was);
            table[key] = table.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        output.WriteLine("");
        output.WriteLine($"  this run's outcome against the baseline sweep's verdict ({missing} edges not in the baseline):");
        output.WriteLine($"    {"outcome",-14} {"border",8} {"grows",8} {"single",8} {"never",8}");
        foreach (var outcome in Enum.GetValues<CoverageEdgeOutcome>())
        {
            var row = new[] { "border", "grows", "single", "never" }
                .Select(k => table.TryGetValue((outcome, k), out var c) ? c : 0).ToArray();
            if (row.Sum() > 0)
            {
                output.WriteLine($"    {outcome,-14} {row[0],8} {row[1],8} {row[2],8} {row[3],8}");
            }
        }
    }

    /// <summary>Reads back the rows this probe wrote, so the summary can be revised without re-reading
    /// the corpus. The column order is the header written above.</summary>
    private static List<(string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)> ReadRows(string path)
    {
        var rows = new List<(string, string, int, double, int, CoverageEdgeOutcome)>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // The file name is quoted and contains commas; everything after it does not.
            var close = line.LastIndexOf('"');
            var name = line[1..close];
            var rest = line[(close + 2)..].Split(',');
            rows.Add((name, rest[0], int.Parse(rest[3], CultureInfo.InvariantCulture),
                double.Parse(rest[4], CultureInfo.InvariantCulture),
                int.Parse(rest[5], CultureInfo.InvariantCulture),
                Enum.Parse<CoverageEdgeOutcome>(rest[7])));
        }

        return rows;
    }

    private static int Count(
        (string File, string Edge, int Span, double Search, int SettleDepth, CoverageEdgeOutcome Outcome)[] at,
        CoverageEdgeOutcome outcome)
        => at.Count(r => r.Outcome == outcome);

    /// <summary>Nearest-rank percentile over an already-sorted array; NaN when it is empty.</summary>
    private static double Pct(double[] sorted, double q)
        => sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>The shallowest swept cap at which one edge is actually trimmed (or found clean), or null
    /// if no swept value reaches its settle depth.</summary>
    private static double? TrimsAtFraction(Image image, PixelRect full, Func<CoverageEdgeTrims, CoverageEdgeTrim> pick)
    {
        foreach (var f in Fractions)
        {
            var t = CoverageEdgeWalk.Measure(image, full, CoverageEdgeWalkOptions.Default with { MaxTrimFraction = f });
            if (pick(t).Outcome is CoverageEdgeOutcome.Trimmed or CoverageEdgeOutcome.Clean)
            {
                return f;
            }
        }

        return null;
    }
}
