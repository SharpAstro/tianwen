namespace SkyTrendProbe;

/// <summary>Synthetic series with known stretches, a fall-back, a kept-rising rise and a live-judge pause.</summary>
public static class SelfCheck
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 20, 0, 0, TimeSpan.Zero);

    private static FrameRow Row(string series, int minute, float sky = 100f, float flux = 1000f, bool marked = false, bool verdictCloud = false,
        bool run = false, bool night = false, bool rise = false, float overTrend = float.NaN, bool liveJudged = true, bool liveCloud = false)
        => new(series, $"{series}_{minute:D4}.fits", marked ? "BAD" : "", marked, T0.AddMinutes(minute), 60, "g", "rig", sky, 1f, 100, flux,
            verdictCloud ? "Cloud" : "None", verdictCloud, run, 1f, 1f, overTrend, rise, false, night, liveJudged, liveCloud, -30, -20, 0.1, 90, 45, -35, 150, "header");

    public static int Run()
    {
        var failures = new List<string>();
        void Check(string what, bool ok)
        {
            if (!ok)
            {
                failures.Add(what);
            }
        }
        static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

        // Series A: 120 one-minute frames. Marked 70..99 (30 frames). The verdict calls 70..84 cloud (rise on 70..79, run on 80..84)
        // and reads 85..99 clear again (the trend caught up): the 51-minute cliff in miniature.
        List<FrameRow> a = [];
        for (var m = 0; m < 120; m++)
        {
            var marked = m is >= 70 and <= 99;
            var cloud = m is >= 70 and <= 84;
            a.Add(Row("A", m, marked: marked, verdictCloud: cloud, rise: m is >= 70 and <= 79, run: m is >= 80 and <= 84,
                night: m == 70, flux: marked ? 500f : 1000f));
        }
        var stretches = SkyTrendAnalysis.Stretches(a);
        Check("A has one stretch", stretches.Count == 1);
        if (stretches.Count == 1)
        {
            var s = stretches[0];
            Check("A stretch frames", s.Frames == 30);
            Check("A stretch minutes", Near(s.Minutes, 29));
            Check("A caught", s.Caught == 15);
            Check("A rise caught", s.CaughtRise == 10);
            Check("A run caught", s.CaughtRun == 5);
            Check("A night caught", s.CaughtNight == 1);
            Check("A read clear again", s.ReadClearAgain == 15);
            Check("A flux in stretch is half the hour before", Near(SkyTrendAnalysis.FluxRatio(a, s), 0.5));
        }

        // Series B: sky rises on frames 40..49 (ratio 1.1 over a 100 line) and falls back at 50: FELL-BACK.
        List<FrameRow> b = [];
        for (var m = 0; m < 80; m++)
        {
            var rises = m is >= 40 and <= 49;
            b.Add(Row("B", m, sky: rises ? 110f : 100f, rise: rises, overTrend: rises ? 1.1f : 1f, flux: 1000f));
        }
        var br = SkyTrendAnalysis.RiseStretches(b);
        Check("B has one rise", br.Count == 1);
        if (br.Count == 1)
        {
            Check("B fell back", br[0].Outcome == RiseOutcome.FellBack);
            Check("B line", Near(br[0].PreRiseLine, 100, 1e-3));
            Check("B peak over line", Near(br[0].PeakOverLine, 1.1, 1e-3));
            Check("B frames", br[0].Frames == 10);
            Check("B flux unchanged", Near(br[0].FluxRatio, 1.0));
        }

        // Series C: the sky rises from frame 60 and is still up when the series ends: KEPT-RISING-TO-END, flux dropping with the sky unchanged.
        List<FrameRow> c = [];
        for (var m = 0; m < 80; m++)
        {
            var up = m >= 60;
            c.Add(Row("C", m, sky: up ? 100f + (m - 59) : 100f, rise: up, overTrend: up ? 1f + (m - 59) / 100f : 1f, marked: up && m >= 70,
                flux: 1000f));
        }
        var cr = SkyTrendAnalysis.RiseStretches(c);
        Check("C has one rise", cr.Count == 1);
        if (cr.Count == 1)
        {
            Check("C kept rising to end", cr[0].Outcome == RiseOutcome.KeptRisingToEnd);
            Check("C marked frames", cr[0].MarkedFrames == 10);
            Check("C minutes", Near(cr[0].Minutes, 19));
        }

        // Live replay: a 20-minute cloud spell (frames 10..29, on marked frames) passes the 15 minute wait at frame 25; a 5-minute spell
        // (frames 50..54) does not, and sits on unmarked frames.
        List<FrameRow> d = [];
        for (var m = 0; m < 80; m++)
        {
            var spell1 = m is >= 10 and <= 29;
            var spell2 = m is >= 50 and <= 54;
            d.Add(Row("D", m, marked: spell1, liveCloud: spell1 || spell2, liveJudged: m != 70));
        }
        var live = SkyTrendAnalysis.ReplayLive(d);
        Check("D first maybe cloud", live.FirstMaybeCloud == T0.AddMinutes(10));
        Check("D give up at frame 25", live.FirstGiveUp == T0.AddMinutes(25));
        Check("D give up lies on marked frames", live.GiveUpOnMarked);
        Check("D spells", live.Spells.Count == 3);
        if (live.Spells.Count == 3)
        {
            Check("D spell 1 timed out", live.Spells[0].PassedTimeout && Near(live.Spells[0].Minutes, 15));
            Check("D spell 2 is the remainder, not timed out", !live.Spells[1].PassedTimeout && live.Spells[1].Frames == 4);
            Check("D spell 3 not timed out", !live.Spells[2].PassedTimeout && live.Spells[2].Frames == 5 && live.Spells[2].MarkedFrames == 0);
        }

        // Precision and recall: A's verdict against A's marks: 15 of 30 marked caught, none unmarked.
        var verdict = SkyTrendAnalysis.ScoreAgainstMarks("verdict", a, r => r.VerdictCloud);
        Check("A recall 0.5", Near(verdict.Recall, 0.5));
        Check("A precision 1", Near(verdict.Precision, 1));
        var riseScore = SkyTrendAnalysis.ScoreAgainstMarks("rise", b, r => r.SkyRising);
        Check("B rise precision 0 (no marks)", Near(riseScore.Precision, 0) && double.IsNaN(riseScore.Recall));

        // The sky's rise per minute: 100 + 0.5 t over 30 minutes is 0.5 / mean (about 107.5) per minute.
        List<FrameRow> e = [];
        for (var m = 0; m <= 30; m++)
        {
            e.Add(Row("E", m, sky: 100f + 0.5f * m));
        }
        Check("E rise per minute", Near(SkyTrendAnalysis.RisePerMinute(e, T0, T0.AddMinutes(30)), 0.5 / 107.5, 1e-6));
        Check("E too few frames", double.IsNaN(SkyTrendAnalysis.RisePerMinute(e, T0, T0.AddMinutes(1))));

        // Moonlight: nothing with the Moon down, more from a quarter than a crescent, more when higher, less when further away.
        Check("moon down adds nothing", Near(Moonlight.SkyFactor(-3, 90, 60, 50, 100), 1));
        var crescent = Moonlight.SkyFactor(20, new Moonlight.Scenario("c", 0.25, 0).PhaseAngleDeg, 60, 50, 100);
        var quarter = Moonlight.SkyFactor(20, new Moonlight.Scenario("q", 0.5, 0).PhaseAngleDeg, 60, 50, 100);
        Check("quarter brighter than crescent", quarter > crescent && crescent > 1);
        Check("a higher Moon is brighter", Moonlight.SkyFactor(40, 90, 60, 50, 100) > Moonlight.SkyFactor(10, 90, 60, 50, 100));
        Check("a farther Moon is dimmer", Moonlight.SkyFactor(30, 90, 120, 50, 100) < Moonlight.SkyFactor(30, 90, 30, 50, 100));
        Check("quarter phase angle is 90", Near(new Moonlight.Scenario("q", 0.5, 0).PhaseAngleDeg, 90, 1e-9));
        var sc = Moonlight.Scenarios[0];
        Check("scenario Moon is down before rising", sc.AltitudeAt(T0, T0, T0.AddHours(4)) < 0);
        Check("scenario Moon is up after rising", sc.AltitudeAt(T0.AddHours(3), T0, T0.AddHours(4)) > 0);

        if (failures.Count == 0)
        {
            Console.WriteLine("self-check: all passed");
            return 0;
        }
        foreach (var f in failures)
        {
            Console.Error.WriteLine($"self-check FAILED: {f}");
        }
        return 1;
    }
}
