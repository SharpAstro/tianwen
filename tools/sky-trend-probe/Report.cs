using System.Globalization;
using System.Text;

namespace SkyTrendProbe;

/// <summary>Writes the CSVs and the summary for a set of rows. Only the output folder is ever written.</summary>
public static class Report
{
    public static void Write(string outDir, string prefix, IReadOnlyList<FrameRow> rows)
    {
        var series = SkyTrendAnalysis.SeriesOf(rows);

        Csv(Path.Combine(outDir, prefix + "frames.csv"),
            ["series", "file", "mark", "marked_cloud", "date_obs", "exposure_s", "group", "rig", "sky", "patchiness", "stars", "flux50_median",
             "causes", "verdict_cloud", "in_cloud_run", "night_sky", "shown", "sky_over_trend", "sky_rising", "term_patchy_or_other", "term_night",
             "live_judged", "live_cloud", "sun_alt_deg", "moon_alt_deg", "moon_illum", "moon_sep_deg", "obj_alt_deg", "lat", "lon", "site_source", "live_over_trend", "live_sky_rising"],
            series.SelectMany(s => s).Select(r => new object?[]
            {
                r.Series, r.File, r.Mark, r.MarkedCloud, r.Taken.ToString("o", CultureInfo.InvariantCulture), r.ExposureS, r.Group, r.Rig, r.Sky,
                r.Patchiness, r.Stars, r.Flux, r.Causes, r.VerdictCloud, r.InCloudRun, r.NightSky, r.Shown, r.SkyOverTrend, r.SkyRising,
                r.TermPatchy, r.TermNight, r.LiveJudged, r.LiveCloud, r.SunAltDeg, r.MoonAltDeg, r.MoonIllum, r.MoonSepDeg, r.ObjAltDeg,
                r.Latitude, r.Longitude, r.SiteSource, r.LiveOverTrend, r.LiveSkyRising,
            }));

        var stretches = series.SelectMany(s => SkyTrendAnalysis.Stretches(s).Select(t => (s, t))).ToList();
        Csv(Path.Combine(outDir, prefix + "stretches.csv"),
            ["series", "start", "end", "minutes", "frames", "caught", "caught_patchy_or_other", "caught_run", "caught_night", "caught_rise",
             "read_clear_again", "flux_vs_hour_before", "rise_per_min_in_stretch", "sun_alt_start", "sun_alt_end"],
            stretches.Select(x => new object?[]
            {
                x.t.Series, Iso(x.t.Start), Iso(x.t.End), x.t.Minutes, x.t.Frames, x.t.Caught, x.t.CaughtPatchy, x.t.CaughtRun, x.t.CaughtNight,
                x.t.CaughtRise, x.t.ReadClearAgain, SkyTrendAnalysis.FluxRatio(x.s, x.t), SkyTrendAnalysis.RisePerMinute(x.s, x.t.Start, x.t.End),
                x.s.First(r => r.Taken == x.t.Start).SunAltDeg, x.s.First(r => r.Taken == x.t.End).SunAltDeg,
            }));

        var rises = series.SelectMany(SkyTrendAnalysis.RiseStretches).ToList();
        Csv(Path.Combine(outDir, prefix + "rise-stretches.csv"),
            ["series", "start", "end", "minutes", "frames", "outcome", "pre_rise_line", "peak_over_line", "sun_alt_start", "sun_alt_end",
             "marked_frames", "flux_vs_hour_before"],
            rises.Select(x => new object?[]
            {
                x.Series, Iso(x.Start), Iso(x.End), x.Minutes, x.Frames, x.Outcome == RiseOutcome.FellBack ? "FELL-BACK" : "KEPT-RISING-TO-END",
                x.PreRiseLine, x.PeakOverLine, x.SunAltStart, x.SunAltEnd, x.MarkedFrames, x.FluxRatio,
            }));

        var replays = series.Select(s => (s, r: SkyTrendAnalysis.ReplayLive(s))).ToList();
        Csv(Path.Combine(outDir, prefix + "live.csv"),
            ["series", "spell_start", "spell_end", "minutes", "frames", "marked_frames", "passed_cloud_wait", "timeout_at", "first_maybe_cloud", "first_give_up", "give_up_on_marked"],
            replays.SelectMany(x => x.r.Spells.Select(sp => new object?[]
            {
                x.s[0].Series, Iso(sp.Start), Iso(sp.End), sp.Minutes, sp.Frames, sp.MarkedFrames, sp.PassedTimeout, sp.TimeoutAt is { } t ? Iso(t) : null,
                x.r.FirstMaybeCloud is { } f ? Iso(f) : null, x.r.FirstGiveUp is { } g ? Iso(g) : null, x.r.GiveUpOnMarked,
            })));

        var scored = new[]
        {
            SkyTrendAnalysis.ScoreAgainstMarks("verdict cloud (any term)", rows, r => r.VerdictCloud),
            SkyTrendAnalysis.ScoreAgainstMarks("sky rising (#2030)", rows, r => r.SkyRising),
            SkyTrendAnalysis.ScoreAgainstMarks("cloud run", rows, r => r.InCloudRun),
            SkyTrendAnalysis.ScoreAgainstMarks("patchiness or other", rows, r => r.TermPatchy),
            SkyTrendAnalysis.ScoreAgainstMarks("night sky", rows, r => r.TermNight),
            SkyTrendAnalysis.ScoreAgainstMarks("live judge (MAYBE_CLOUD_)", rows.Where(r => r.LiveJudged), r => r.LiveCloud),
        };
        Csv(Path.Combine(outDir, prefix + "scores.csv"), ["term", "tp", "fp", "fn", "tn", "precision", "recall"],
            scored.Select(s => new object?[] { s.Name, s.Tp, s.Fp, s.Fn, s.Tn, s.Precision, s.Recall }));

        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine(string.Create(ci, $"# Sky-trend probe {prefix.TrimEnd('-')}"));
        sb.AppendLine(string.Create(ci, $"\n{rows.Count} frames in {series.Count} series; {rows.Count(r => r.MarkedCloud)} marked cloud; {stretches.Count} marked stretches; {rises.Count} rising stretches ({rises.Count(r => r.Outcome == RiseOutcome.FellBack)} fell back, {rises.Count(r => r.Outcome == RiseOutcome.KeptRisingToEnd)} kept rising to the end).\n"));
        sb.AppendLine("| term | TP | FP | FN | precision | recall |\n|---|---|---|---|---|---|");
        foreach (var s in scored)
        {
            sb.AppendLine(string.Create(ci, $"| {s.Name} | {s.Tp} | {s.Fp} | {s.Fn} | {s.Precision:0.00} | {s.Recall:0.00} |"));
        }
        File.WriteAllText(Path.Combine(outDir, prefix + "summary.md"), sb.ToString().ReplaceLineEndings("\n"));
        Console.Write(sb.ToString());
    }

    private static string Iso(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    private static void Csv(string path, string[] header, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header)).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(Format))).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        bool b => b ? "1" : "0",
        float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "",
        double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "",
        string s => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s,
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
