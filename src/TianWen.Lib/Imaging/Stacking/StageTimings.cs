using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Wall-clock accounting for a pipeline run, one accumulator per session (or per stack), recording
/// each stage's elapsed time <b>together with the work it did</b> so throughput is a stored
/// measurement rather than something a reader divides out later.
///
/// <para><b>Why this exists.</b> There was no timing instrumentation anywhere in the dataset path, so
/// answering "how fast is integration, in pixels per second" meant parsing timestamps out of the
/// Debug file log and reconstructing the stage boundaries from log message shapes. That works, and it
/// produced the first real numbers, but it is fragile in a specific way: the reconstruction has to
/// GUESS the denominator, and it guessed wrong. Export was normalised per input frame, which made it
/// look like a 13.9 Mpx/s compute stage, when it actually writes eleven 256x256x3 tiles per cell and
/// is bound by creating 41 files a second on a spindle. Recording the volume beside the duration, at
/// the site that knows it, is the fix.</para>
///
/// <para><b>Items, not frames.</b> <see cref="Stage.Items"/> is whatever unit that stage repeats:
/// subs for measure/register/warp/integrate, tiles for export. Naming it "frames" would be wrong for
/// exactly the stage whose denominator was already got wrong once. <see cref="Stage.Pixels"/> is
/// always a pixel count, so <see cref="Stage.MegapixelsPerSecond"/> stays comparable across stages
/// that repeat over different things.</para>
///
/// <para><b>Not thread-safe, deliberately.</b> One instance belongs to one session, and the pipeline
/// processes sessions one at a time (parallelism inside this codebase is intra-frame:
/// <c>Parallel.For</c> over rows, never over frames). A shared instance across concurrent sessions
/// would need a lock on a path that runs thousands of times per session, to protect a diagnostic.
/// Give each unit of work its own and <see cref="Merge"/> them at the end.</para>
///
/// <para>Placed in <c>Imaging/Stacking</c> rather than beside the dataset builder so
/// <c>StackingPipeline</c> can adopt it when the dataset registrar collapses onto the stacking core,
/// rather than growing a second implementation there.</para>
/// </summary>
public sealed class StageTimings
{
    /// <summary>
    /// One stage's accumulated cost. <paramref name="Seconds"/> rather than a <see cref="TimeSpan"/>
    /// because this type exists to be analysed: it is persisted as JSON and read back by report code
    /// and by ad-hoc scripts, and a TimeSpan crosses as an ISO-8601 duration string that every one of
    /// those consumers would have to parse before it could do arithmetic.
    /// </summary>
    /// <param name="Name">Stage name, from <see cref="StageNames"/> for the well-known ones.</param>
    /// <param name="Seconds">Total wall time attributed to this stage.</param>
    /// <param name="Items">Work items repeated: subs, or tiles for an export stage.</param>
    /// <param name="Pixels">Pixels read or written, whichever the stage is bound by.</param>
    public readonly record struct Stage(string Name, double Seconds, long Items, long Pixels)
    {
        /// <summary>Mean cost of one item, or 0 when the stage recorded none.</summary>
        /// <remarks><see cref="JsonIgnoreAttribute"/> because it is DERIVED. A get-only property is
        /// serialized by default, which quietly persisted this beside the numbers it is computed from
        /// -- the same "two stored renderings of one measurement" defect this type's own remarks warn
        /// about, and it doubled the store's size to say nothing new. A consumer divides.</remarks>
        [JsonIgnore]
        public double MillisecondsPerItem => Items > 0 ? Seconds * 1000.0 / Items : 0.0;

        /// <summary>Pixel throughput, or 0 when the stage recorded no pixels or took no time.</summary>
        /// <remarks><inheritdoc cref="MillisecondsPerItem" path="/remarks/node()"/></remarks>
        [JsonIgnore]
        public double MegapixelsPerSecond => Seconds > 0 && Pixels > 0 ? Pixels / 1e6 / Seconds : 0.0;
    }

    private readonly List<Stage> _stages = [];

    /// <summary>
    /// Timestamp to pass back to <see cref="Record"/>. A raw <see cref="Stopwatch"/> tick rather than
    /// a <see cref="Stopwatch"/> instance so starting a measurement allocates nothing and a stage that
    /// is never recorded costs one QPC read.
    /// </summary>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Attributes the time since <paramref name="startTimestamp"/> to <paramref name="stage"/>,
    /// ACCUMULATING when that stage is recorded more than once (the half-master pair records twice,
    /// once per half, and reads as one stage). Stage order in <see cref="Snapshot"/> is first-record
    /// order, which is pipeline order for free.
    /// </summary>
    /// <param name="stage">Stage name; use a <see cref="StageNames"/> constant where one fits.</param>
    /// <param name="startTimestamp">The value <see cref="Start"/> returned before the work.</param>
    /// <param name="items">Work items this call processed. Added to any prior total.</param>
    /// <param name="pixels">Pixels this call read or wrote. Added to any prior total.</param>
    public void Record(string stage, long startTimestamp, long items = 0, long pixels = 0)
    {
        var seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        for (var i = 0; i < _stages.Count; i++)
        {
            if (string.Equals(_stages[i].Name, stage, StringComparison.Ordinal))
            {
                var prior = _stages[i];
                _stages[i] = prior with
                {
                    Seconds = prior.Seconds + seconds,
                    Items = prior.Items + items,
                    Pixels = prior.Pixels + pixels,
                };
                return;
            }
        }
        _stages.Add(new Stage(stage, seconds, items, pixels));
    }

    /// <summary>The recorded stages, in first-record order.</summary>
    public ImmutableArray<Stage> Snapshot() => [.. _stages];

    /// <summary>Sum of every recorded stage. Not the same as the caller's total wall time: whatever
    /// sits between stages is unaccounted, and that gap is worth seeing rather than hiding.</summary>
    public double TotalSeconds
    {
        get
        {
            var total = 0.0;
            foreach (var s in _stages)
            {
                total += s.Seconds;
            }
            return total;
        }
    }

    /// <summary>
    /// Folds many runs' stages into one set, summing by name and keeping first-seen order. Used for
    /// the run-level roll-up over every session.
    /// </summary>
    public static ImmutableArray<Stage> Merge(IEnumerable<ImmutableArray<Stage>> runs)
    {
        var merged = new List<Stage>();
        foreach (var run in runs)
        {
            foreach (var stage in run)
            {
                var found = false;
                for (var i = 0; i < merged.Count; i++)
                {
                    if (string.Equals(merged[i].Name, stage.Name, StringComparison.Ordinal))
                    {
                        var prior = merged[i];
                        merged[i] = prior with
                        {
                            Seconds = prior.Seconds + stage.Seconds,
                            Items = prior.Items + stage.Items,
                            Pixels = prior.Pixels + stage.Pixels,
                        };
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    merged.Add(stage);
                }
            }
        }
        return [.. merged];
    }

    /// <summary>
    /// One log line: total, then each stage with its elapsed and per-item cost. Rendered on demand
    /// and never persisted alongside the numbers, for the same reason the registration census is not:
    /// two stored renderings of one measurement is how one of them goes stale.
    /// </summary>
    public string Describe() => Describe(Snapshot());

    /// <inheritdoc cref="Describe()"/>
    public static string Describe(ImmutableArray<Stage> stages)
    {
        if (stages.IsDefaultOrEmpty)
        {
            return "no stages timed";
        }
        var total = 0.0;
        foreach (var s in stages)
        {
            total += s.Seconds;
        }
        var sb = new StringBuilder();
        sb.Append(total.ToString("F1", CultureInfo.InvariantCulture)).Append("s total");
        foreach (var s in stages)
        {
            sb.Append(" | ").Append(s.Name).Append(' ')
              .Append(s.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append('s');
            if (s.Items > 0)
            {
                sb.Append(' ').Append(s.MillisecondsPerItem.ToString("F0", CultureInfo.InvariantCulture)).Append("ms/it");
            }
            if (s.Pixels > 0 && s.Seconds > 0)
            {
                sb.Append(' ').Append(s.MegapixelsPerSecond.ToString("F1", CultureInfo.InvariantCulture)).Append("Mpx/s");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The run-level roll-up as a fixed-width table, one row per stage plus a total. Multi-line, so
    /// it belongs in a summary at the end of a run, not in a per-session line.
    ///
    /// <para><paramref name="wallSeconds"/> is the caller's OWN measured wall time, not the sum of
    /// the stages, so the table can show what is unaccounted. A large unaccounted share means the
    /// stage boundaries have drifted from where the time actually goes, which is precisely the thing
    /// a timing table should be able to tell you about itself.</para>
    /// </summary>
    public static string DescribeTable(ImmutableArray<Stage> stages, double wallSeconds = 0.0)
    {
        if (stages.IsDefaultOrEmpty)
        {
            return "no stages timed";
        }
        var accounted = 0.0;
        foreach (var s in stages)
        {
            accounted += s.Seconds;
        }
        var denominator = wallSeconds > 0 ? wallSeconds : accounted;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"{"stage",-12}{"wall",10}{"share",8}{"items",10}{"per item",12}{"Mpx/s",9}");
        foreach (var s in stages)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"{s.Name,-12}{s.Seconds / 60.0,10:F1}{100.0 * s.Seconds / denominator,7:F1}%{s.Items,10}");
            sb.Append(s.Items > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0,9:F0} ms", s.MillisecondsPerItem)
                : new string(' ', 12));
            sb.Append(s.Pixels > 0 && s.Seconds > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0,9:F1}", s.MegapixelsPerSecond)
                : new string(' ', 9));
        }
        if (wallSeconds > 0)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"{"unaccounted",-12}{(wallSeconds - accounted) / 60.0,10:F1}{100.0 * (wallSeconds - accounted) / denominator,7:F1}%");
        }
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"{"TOTAL",-12}{denominator / 60.0,10:F1} min");
        return sb.ToString();
    }
}

/// <summary>
/// The well-known stage names, so a stage is spelled once. Not an enum: the layer that owns tile
/// export and the PSF measurement sits above this assembly and has its own stages, and the names
/// have to survive into JSON readably for the analysis this data exists to serve.
/// </summary>
public static class StageNames
{
    /// <summary>Per light: load, calibrate, debayer, detect stars, derive PSF metrics. Items = lights.</summary>
    public const string Measure = "measure";

    /// <summary>Quad forming plus the tolerance-ladder match against the reference. Items = survivors.
    /// Touches only star centroids, never pixels, which is why it costs a fraction of the others.</summary>
    public const string Register = "register";

    /// <summary>Reload, calibrate, debayer, warp onto the union canvas, write the scratch FITS.
    /// Items = registered subs. Kept apart from <see cref="Integrate"/> even though both are one
    /// block in the log, because it is a second full pass over the raw lights and reads from a
    /// different disk than the integration that follows it.</summary>
    public const string Warp = "warp";

    /// <summary>Session master integration from the warped scratch subs. Items = subs.</summary>
    public const string Integrate = "integrate";

    /// <summary>Both half-master integrations. Items = subs across the two halves, so it is directly
    /// comparable per item with <see cref="Integrate"/>.</summary>
    public const string Halves = "halves";

    /// <summary>Resolving and building the bias/dark/flat masters for the session. Items = masters
    /// built; a cache hit legitimately records time with no items.</summary>
    public const string Calibrate = "calibrate";
}
