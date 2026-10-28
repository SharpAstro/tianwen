using Shouldly;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The outer halo (#1870): the second, wide, faint Moffat under an injected star, its fit per rig to the census's LINEAR
/// clipped-star profiles, the reach it gives a star's drawing window, and what it is through the export's input stretch.
/// </summary>
[Collection("Imaging")]
public class OuterHaloTests(ITestOutputHelper output)
{
    private static readonly OuterHalo Known = new(4e-3, 6.0, 1.2);

    private static StarProfile Core() => StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);

    [Fact]
    public void DefaultIsNoneAndDrawsNothing()
    {
        default(OuterHalo).IsNone.ShouldBeTrue();
        default(OuterHalo).At(3, 4).ShouldBe(0.0);
        default(OuterHalo).PixelMean(3, 4).ShouldBe(0.0);
        default(OuterHalo).RadiusAtFraction(1e-4).ShouldBe(0.0);
        // A profile with the default term is the profile it was, to the bit.
        var plain = Core();
        var withDefault = plain with { Outer = default };
        for (var dx = -9; dx <= 9; dx += 3)
        {
            plain.PixelMean(10 + dx, 10, 10.3, 9.8).ShouldBe(withDefault.PixelMean(10 + dx, 10, 10.3, 9.8));
        }
    }

    [Fact]
    public void TheTermIsAMoffatOfItsFractionScaleAndExponent()
    {
        Known.At(0, 0).ShouldBe(4e-3, 1e-15);
        Known.At(6, 0).ShouldBe(4e-3 * Math.Pow(2.0, -1.2), 1e-15);
        Known.At(0, 6).ShouldBe(Known.At(6, 0), 1e-15, "round");
        Known.AtRadius(20).ShouldBe(Known.At(12, 16), 1e-15);
    }

    [Fact]
    public void ThePixelMeanIsTheQuadratureNearTheCentreAndTheCentreValueFar()
    {
        // Near: two points an axis, the same integration the profile's pixels take; a smooth term's mean is its centre value to
        // second order in the pixel.
        Known.PixelMean(3, 2).ShouldBe(Known.At(3, 2), Known.At(3, 2) * 1e-3);
        Known.PixelMean(40, 0).ShouldBe(Known.At(40, 0));
        // The mean of a convex term over a pixel is at or above its centre value.
        Known.PixelMean(3, 2).ShouldBeGreaterThanOrEqualTo(Known.At(3, 2));
    }

    [Fact]
    public void AStarProfileAddsTheTermUnderItsOwnLight()
    {
        var plain = Core();
        var halo = plain with { Outer = Known };
        foreach (var (px, py) in new[] { (10, 10), (14, 10), (30, 25), (60, 10) })
        {
            var added = halo.PixelMean(px, py, 10.0, 10.0) - plain.PixelMean(px, py, 10.0, 10.0);
            added.ShouldBe(Known.PixelMean(px - 10.0, py - 10.0), 1e-15);
        }
    }

    [Fact]
    public void TheTermScalesWithTheStarsAmplitudeInTheRenderedPlane()
    {
        const int size = 129;
        var plate = new[] { new float[size * size] };
        InjectionRender Render(double amplitude, OuterHalo outer)
            => StarInjection.Render(plate, size, size, null,
                [new InjectedStar(64, 64, [amplitude], [Core() with { Outer = outer }], false, ImmutableArray<double>.Empty)],
                [1e-7], new Random(1));
        var bare = Render(1.0, default);
        var one = Render(1.0, Known);
        var ten = Render(10.0, Known);
        foreach (var r in new[] { 4, 10, 20, 30 })
        {
            var i = (64 * size) + 64 + r;
            var excess1 = one.Planes[0][i] - bare.Planes[0][i];
            var excess10 = ten.Planes[0][i] - (10.0 * bare.Planes[0][i]);
            excess1.ShouldBe((float)Known.PixelMean(r, 0), 1e-7f);
            excess10.ShouldBe(10.0 * excess1, 1e-6, $"ten times the peak is ten times the halo at {r} px");
        }
    }

    [Fact]
    public void TheDrawingWindowReachesAsFarAsTheHaloDoes()
    {
        var plain = Core();
        var halo = plain with { Outer = Known };
        const double floor = 1e-5;
        var reachPlain = plain.RadiusAtFraction(floor);
        var reachHalo = halo.RadiusAtFraction(floor);
        reachHalo.ShouldBeGreaterThan(reachPlain * 2, "the old Moffat's reach would cut the term");
        // The term is at the floor at its own reach, and not above it past that.
        Known.At(reachHalo, 0).ShouldBe(floor, floor * 1e-6);
        Known.At(reachHalo + 1, 0).ShouldBeLessThan(floor);
        // Never less than the profile's own, and none for a fraction the term never reaches.
        halo.RadiusAtFraction(0.3).ShouldBe(plain.RadiusAtFraction(0.3), 1e-12);
        Known.RadiusAtFraction(0.5).ShouldBe(0.0);
    }

    [Fact]
    public void ThePerStarCostStaysBoundedAndIsReported()
    {
        const int size = 301;
        var plate = new[] { new float[size * size] };
        InjectedStar Star(OuterHalo outer) => new(150, 150, [50.0], [Core() with { Outer = outer }], false, ImmutableArray<double>.Empty);
        var floor = new[] { 1e-6 };
        // Warm both paths, then time many stars of each.
        StarInjection.Render(plate, size, size, null, [Star(default)], floor, new Random(1));
        StarInjection.Render(plate, size, size, null, [Star(Known)], floor, new Random(1));
        const int stars = 40;
        var bare = Enumerable.Repeat(Star(default), stars).ToArray();
        var halo = Enumerable.Repeat(Star(Known), stars).ToArray();
        var sw = Stopwatch.StartNew();
        StarInjection.Render(plate, size, size, null, bare, floor, new Random(1));
        var bareMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var render = StarInjection.Render(plate, size, size, null, halo, floor, new Random(1));
        var haloMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{stars} amplitude-50 stars, floor 1e-6: reach {Core().RadiusAtFraction(1e-6 / 50):F0} px bare, {(Core() with { Outer = Known }).RadiusAtFraction(1e-6 / 50):F0} px with the halo; {bareMs / stars:F2} ms a star bare, {haloMs / stars:F2} ms with"));
        (Core() with { Outer = Known }).RadiusAtFraction(1e-6 / 50).ShouldBeLessThanOrEqualTo(StarInjection.MaxRadiusPx, "never past the window's cap");
        render.Planes[0][(150 * size) + 150 + 60].ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void TheFitRecoversKnownParameters()
    {
        var truth = new double[41];
        for (var r = 0; r < truth.Length; r++)
        {
            truth[r] = Known.AtRadius(r);
        }
        var fit = OuterHaloFit.Fit(truth).ShouldNotBeNull();
        fit.Halo.Fraction.ShouldBe(Known.Fraction, Known.Fraction * 0.02);
        fit.Halo.AlphaPx.ShouldBe(Known.AlphaPx, Known.AlphaPx * 0.02);
        fit.Halo.Beta.ShouldBe(Known.Beta, 0.02);
        fit.RmsLog.ShouldBeLessThan(0.01);
        fit.TailLog.ShouldBeLessThan(0.02);
        // Too few positive values to fit.
        OuterHaloFit.Fit(new double[41]).ShouldBeNull();
    }

    [Fact]
    public void TheFitIsReadPerRigFromTheProfileTableLessWhatTheInjectorDrawsAlready()
    {
        // A synthetic table: one rig whose real L median is the known halo plus a Moffat the injector draws (synth11), a rig
        // with R, G and B, and a rig with no real set.
        string Array(Func<int, double> f) => "[" + string.Join(",", Enumerable.Range(0, 41).Select(r => f(r).ToString("R", CultureInfo.InvariantCulture))) + "]";
        double Drawn(int r) => 0.05 * Math.Pow(1.0 + (r * r / 9.0), -3.0);
        string Group(double stars, string median) => $"{{\"stars\":{stars.ToString(CultureInfo.InvariantCulture)},\"channels\":{{\"L\":{{\"linear\":{{\"median\":{median}}}}}}}}}";
        string Rgb(string median) => string.Join(",", new[] { "R", "G", "B" }.Select(n => $"\"{n}\":{{\"linear\":{{\"median\":{median}}}}}"));
        var both = Array(r => Drawn(r) + Known.AtRadius(r));
        var json = "{\"schema\":1,\"rigs\":{"
            + "\"Rig-A\":{\"real\":{\"core1\":" + Group(100, both) + ",\"core2\":" + Group(50, both) + "},"
            + "\"synth11\":{\"core1\":" + Group(10, Array(Drawn)) + ",\"core2\":" + Group(10, Array(Drawn)) + "}},"
            + "\"Rig-B\":{\"real\":{\"core1\":{\"stars\":30,\"channels\":{" + Rgb(Array(r => Known.AtRadius(r))) + "}}}},"
            + "\"Rig-C\":{\"synth11\":{}}}}";
        using var document = JsonDocument.Parse(json);
        var fit = OuterHaloFit.FromDocument(document.RootElement);
        fit.Has("Rig-A").ShouldBeTrue();
        fit.Has("Rig-C").ShouldBeFalse("a rig without a real set has no fit");
        fit.Has("Rig-Z").ShouldBeFalse();
        var a = fit.For("Rig-A", 1);
        a.Length.ShouldBe(1);
        a[0].Fraction.ShouldBe(Known.Fraction, Known.Fraction * 0.02, "what the injector draws is taken off first");
        a[0].AlphaPx.ShouldBe(Known.AlphaPx, Known.AlphaPx * 0.02);
        // A three-channel master takes the luminance's for every channel where the file has no colour of its own.
        var a3 = fit.For("Rig-A", 3);
        a3.Length.ShouldBe(3);
        a3.ShouldAllBe(h => h == a[0]);
        fit.For("Rig-B", 3).Length.ShouldBe(3);
        fit.For("Rig-B", 3).ShouldAllBe(h => Math.Abs(h.Beta - Known.Beta) < 0.02);
        fit.For("Rig-Z", 3).IsDefault.ShouldBeTrue("a rig the file lacks gets none");
        fit.For("Rig-A", 2).IsDefault.ShouldBeTrue();
        fit.InWords("Rig-A").ShouldContain("L ");
    }

    [Fact]
    public void ThroughTheExportsInputStretchTheHaloIsVisibleOutToThirtyPixels()
    {
        // The input stretch is the export's own MtfStretchWith: a midtones balance chosen to put the sky at a quarter, a minimum
        // taken off. Read the halo as the stretch shows it: excess over the stretched sky, at 4, 10, 20 and 30 px.
        const double sky = 0.02;
        const double target = 0.25;
        // MTF(m, x) = (m - 1) x / ((2m - 1) x - m); solve MTF(m, sky) = target for m.
        var balance = Solve(sky, target);
        double Mtf(double x) => (balance - 1.0) * x / (((2.0 * balance) - 1.0) * x - balance);
        Mtf(sky).ShouldBe(target, 1e-9);

        var plane = new float[1, 42];
        for (var r = 0; r <= 40; r++)
        {
            plane[0, r] = (float)(sky + Known.AtRadius(r));
        }
        plane[0, 41] = (float)sky;
        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        var stretched = image.MtfStretchWith([0f], [balance]);
        var skyStretched = stretched.GetChannelSpan(0)[41];
        skyStretched.ShouldBe((float)target, 1e-5f);
        double Excess(int r) => stretched.GetChannelSpan(0)[r] - skyStretched;
        foreach (var r in new[] { 4, 10, 20, 30 })
        {
            var expected = Mtf(sky + Known.AtRadius(r)) - target;
            Excess(r).ShouldBe(expected, 2e-6, $"the stretched halo at {r} px");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r} px: linear {Known.AtRadius(r):E2}, stretched excess {Excess(r):F4}"));
        }
        Excess(4).ShouldBeGreaterThan(Excess(10));
        Excess(10).ShouldBeGreaterThan(Excess(20));
        Excess(20).ShouldBeGreaterThan(Excess(30));
        Excess(30).ShouldBeGreaterThan(0.001, "a halo the stretch turns up is a visible one out to 30 px");

        static double Solve(double x, double y)
        {
            // y((2m - 1) x - m) = (m - 1) x  =>  m (2xy - y - x) = xy - x  =>  m = x (y - 1) / (2xy - y - x)
            return x * (y - 1.0) / ((2.0 * x * y) - y - x);
        }
    }
}
