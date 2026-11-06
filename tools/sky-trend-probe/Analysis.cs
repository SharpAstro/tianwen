namespace SkyTrendProbe;

/// <summary>One graded light: what the viewer's grade and the live judge said, and where the Sun and Moon were.</summary>
public sealed record FrameRow(
    string Series,
    string File,
    string Mark,
    bool MarkedCloud,
    DateTimeOffset Taken,
    double ExposureS,
    string Group,
    string Rig,
    float Sky,
    float Patchiness,
    int Stars,
    float Flux,
    string Causes,
    bool VerdictCloud,
    bool InCloudRun,
    float NightSky,
    float Shown,
    float SkyOverTrend,
    bool SkyRising,
    bool TermPatchy,
    bool TermNight,
    bool LiveJudged,
    bool LiveCloud,
    double SunAltDeg,
    double MoonAltDeg,
    double MoonIllum,
    double MoonSepDeg,
    double ObjAltDeg,
    double Latitude,
    double Longitude,
    string SiteSource,
    float LiveOverTrend = float.NaN,
    bool LiveSkyRising = false);

public sealed record Stretch(string Series, DateTimeOffset Start, DateTimeOffset End, double Minutes, int Frames, int Caught,
    int CaughtPatchy, int CaughtRun, int CaughtNight, int CaughtRise, int ReadClearAgain);

public enum RiseOutcome { FellBack, KeptRisingToEnd }

public sealed record RiseStretch(string Series, DateTimeOffset Start, DateTimeOffset End, double Minutes, int Frames, RiseOutcome Outcome,
    double PreRiseLine, double PeakOverLine, double SunAltStart, double SunAltEnd, int MarkedFrames, double FluxRatio);

public sealed record LiveSpell(DateTimeOffset Start, DateTimeOffset End, double Minutes, int Frames, int MarkedFrames, bool PassedTimeout,
    DateTimeOffset? TimeoutAt);

public sealed record LiveReplay(DateTimeOffset? FirstMaybeCloud, IReadOnlyList<LiveSpell> Spells, DateTimeOffset? FirstGiveUp, bool GiveUpOnMarked);

public sealed record Score(string Name, int Tp, int Fp, int Fn, int Tn)
{
    public double Precision => Tp + Fp == 0 ? double.NaN : (double)Tp / (Tp + Fp);
    public double Recall => Tp + Fn == 0 ? double.NaN : (double)Tp / (Tp + Fn);
}

/// <summary>Pure functions over the per-frame rows of ONE series in time order (except <see cref="SeriesOf"/>); no I/O.</summary>
public static class SkyTrendAnalysis
{
    /// <summary>The session's CloudWaitTimeout default, which the live replay measures a spell against.</summary>
    public static readonly TimeSpan CloudWait = TimeSpan.FromMinutes(15);

    /// <summary>The rise bar of #2030 (SkyRiseFrom), repeated here so the analysis needs no #2030 symbol.</summary>
    public const double RiseBar = 0.04;

    public static IReadOnlyList<IReadOnlyList<FrameRow>> SeriesOf(IEnumerable<FrameRow> rows)
        => [.. rows.Where(r => r.Taken != default).GroupBy(r => r.Series).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<FrameRow>)[.. g.OrderBy(r => r.Taken)])];

    /// <summary>Runs of consecutive frames the owner marked cloud: how long, how many each term caught, how many read clear again inside.</summary>
    public static IReadOnlyList<Stretch> Stretches(IReadOnlyList<FrameRow> series)
    {
        List<Stretch> result = [];
        var i = 0;
        while (i < series.Count)
        {
            if (!series[i].MarkedCloud)
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < series.Count && series[j + 1].MarkedCloud)
            {
                j++;
            }
            int caught = 0, patchy = 0, run = 0, night = 0, rise = 0, clearAgain = 0;
            var seenCaught = false;
            for (var k = i; k <= j; k++)
            {
                var r = series[k];
                if (r.VerdictCloud)
                {
                    caught++;
                    seenCaught = true;
                }
                else if (seenCaught)
                {
                    clearAgain++;
                }
                patchy += r.TermPatchy ? 1 : 0;
                run += r.InCloudRun ? 1 : 0;
                night += r.TermNight ? 1 : 0;
                rise += r.SkyRising ? 1 : 0;
            }
            result.Add(new Stretch(series[i].Series, series[i].Taken, series[j].Taken, (series[j].Taken - series[i].Taken).TotalMinutes,
                j - i + 1, caught, patchy, run, night, rise, clearAgain));
            i = j + 1;
        }
        return result;
    }

    /// <summary>Runs of consecutive SkyRising frames, each classed by whether the sky fell back under the bar of its pre-rise line.</summary>
    public static IReadOnlyList<RiseStretch> RiseStretches(IReadOnlyList<FrameRow> series)
    {
        List<RiseStretch> result = [];
        var i = 0;
        while (i < series.Count)
        {
            if (!series[i].SkyRising)
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < series.Count && series[j + 1].SkyRising)
            {
                j++;
            }
            var first = series[i];
            // The trend line at the first risen frame: its sky over its ratio to the line.
            var line = float.IsFinite(first.SkyOverTrend) && first.SkyOverTrend > 0 ? first.Sky / first.SkyOverTrend : double.NaN;
            var fell = false;
            for (var k = j + 1; k < series.Count && double.IsFinite(line); k++)
            {
                if (series[k].Sky < line * (1 + RiseBar))
                {
                    fell = true;
                    break;
                }
            }
            var peak = double.IsFinite(line) ? series.Skip(i).Take(j - i + 1).Max(r => r.Sky) / line : double.NaN;
            result.Add(new RiseStretch(first.Series, first.Taken, series[j].Taken, (series[j].Taken - first.Taken).TotalMinutes, j - i + 1,
                fell ? RiseOutcome.FellBack : RiseOutcome.KeptRisingToEnd, line, peak, first.SunAltDeg, series[j].SunAltDeg,
                series.Skip(i).Take(j - i + 1).Count(r => r.MarkedCloud), FluxRatio(series, i, j, TimeSpan.FromHours(1))));
            i = j + 1;
        }
        return result;
    }

    /// <summary>Median star flux over frames [from, to] against the median over the unmarked frames in the hour before; NaN where either is empty.</summary>
    public static double FluxRatio(IReadOnlyList<FrameRow> series, int from, int to, TimeSpan before)
    {
        var inside = series.Skip(from).Take(to - from + 1).Select(r => (double)r.Flux).Where(double.IsFinite).ToList();
        var start = series[from].Taken;
        var prior = series.Where(r => r.Taken < start && r.Taken >= start - before && !r.MarkedCloud && !r.SkyRising)
            .Select(r => (double)r.Flux).Where(double.IsFinite).ToList();
        return inside.Count == 0 || prior.Count == 0 ? double.NaN : Median(inside) / Median(prior);
    }

    /// <summary>The flux ratio of a marked stretch (by its time bounds) against the hour before it.</summary>
    public static double FluxRatio(IReadOnlyList<FrameRow> series, Stretch stretch)
    {
        var from = series.ToList().FindIndex(r => r.Taken == stretch.Start);
        var to = series.ToList().FindIndex(r => r.Taken == stretch.End);
        return from < 0 || to < from ? double.NaN : FluxRatio(series, from, to, TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Replays what the session does with the live verdicts: a cloud spell starts at the first cloud frame and ends at a clear one;
    /// a spell that reaches <see cref="CloudWait"/> is given up (the condition-deterioration path, which moves to the next target).
    /// </summary>
    public static LiveReplay ReplayLive(IReadOnlyList<FrameRow> series, TimeSpan? wait = null)
    {
        var limit = wait ?? CloudWait;
        List<LiveSpell> spells = [];
        DateTimeOffset? firstMaybe = null, firstGiveUp = null;
        var giveUpOnMarked = false;
        DateTimeOffset? since = null, last = null;
        int frames = 0, marked = 0;
        void Close(bool timedOut, DateTimeOffset end, DateTimeOffset? timeoutAt)
        {
            if (since is not { } s)
            {
                return;
            }
            spells.Add(new LiveSpell(s, end, (end - s).TotalMinutes, frames, marked, timedOut, timeoutAt));
            if (timedOut && firstGiveUp is null)
            {
                firstGiveUp = timeoutAt;
                giveUpOnMarked = marked * 2 >= frames;
            }
            since = null;
            frames = marked = 0;
        }
        foreach (var r in series)
        {
            if (!r.LiveJudged)
            {
                continue;
            }
            if (r.LiveCloud)
            {
                firstMaybe ??= r.Taken;
                since ??= r.Taken;
                frames++;
                marked += r.MarkedCloud ? 1 : 0;
                last = r.Taken;
                if (r.Taken - since >= limit)
                {
                    Close(true, r.Taken, r.Taken);
                }
            }
            else
            {
                if (since is not null)
                {
                    Close(false, last ?? r.Taken, null);
                }
            }
        }
        if (since is not null)
        {
            Close(false, last ?? since.Value, null);
        }
        return new LiveReplay(firstMaybe, spells, firstGiveUp, giveUpOnMarked);
    }

    /// <summary>Confusion counts of a per-frame prediction against the owner's cloud marks.</summary>
    public static Score ScoreAgainstMarks(string name, IEnumerable<FrameRow> rows, Func<FrameRow, bool> predicted)
    {
        int tp = 0, fp = 0, fn = 0, tn = 0;
        foreach (var r in rows)
        {
            var p = predicted(r);
            if (r.MarkedCloud)
            {
                if (p) { tp++; } else { fn++; }
            }
            else if (p)
            {
                fp++;
            }
            else
            {
                tn++;
            }
        }
        return new Score(name, tp, fp, fn, tn);
    }

    /// <summary>The sky's change per minute as a fraction of its mean level, a least-squares slope over the frames in [from, to]; NaN under three frames.</summary>
    public static double RisePerMinute(IReadOnlyList<FrameRow> series, DateTimeOffset from, DateTimeOffset to)
    {
        var pts = series.Where(r => r.Taken >= from && r.Taken <= to && float.IsFinite(r.Sky) && r.Sky > 0)
            .Select(r => ((r.Taken - from).TotalMinutes, (double)r.Sky)).ToList();
        if (pts.Count < 3)
        {
            return double.NaN;
        }
        var mx = pts.Average(p => p.Item1);
        var my = pts.Average(p => p.Item2);
        var sxx = pts.Sum(p => (p.Item1 - mx) * (p.Item1 - mx));
        return sxx <= 0 ? double.NaN : pts.Sum(p => (p.Item1 - mx) * (p.Item2 - my)) / sxx / my;
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
