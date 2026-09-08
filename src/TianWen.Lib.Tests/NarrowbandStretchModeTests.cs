using System.Threading.Tasks;
using TianWen.UI.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Auto must not render a narrowband master Linked. Reported 2026-09-06: <i>"Auto mode for HOO SPCC
    /// looks wrong (it switches to Linked which gives it a strong colour cast), unlinked works better
    /// there"</i>.
    /// </summary>
    /// <remarks>
    /// <para>The rule Auto had could not see it: it knows a frame is colour and that a calibration exists,
    /// and both are true of an HOO master. What is false is the calibration's premise. SPCC integrates
    /// stellar SEDs against the system throughput, which assumes a continuum reaches the sensor; through
    /// an Ha + OIII filter almost none does, so the triple is a fit of nothing and Linked preserves it as
    /// a cast.</para>
    /// <para>The frame is classified from the throughputs SPCC itself integrates, never from the filter's
    /// NAME. A name would have to go back through the token matcher, which can answer differently from
    /// what the fit actually used, and <c>Filter.Bandpass</c> is no help at all here: it is populated only
    /// for canonically-named filters, so the real headers this is about ("Ha 3nm", "L-eXtreme", "Antlia
    /// ALP-T") all canonicalise to Unknown and carry <c>Bandpass.None</c>.</para>
    /// </remarks>
    public class NarrowbandStretchModeTests
    {
        /// <summary>
        /// The truth table, including the case the report is about. Note the fourth line: with no
        /// calibration the answer was already Unlinked, so the new fact can only ever move a frame that
        /// has one.
        /// </summary>
        [Theory]
        [InlineData(true, true, false, StretchMode.Linked)]     // an ordinary calibrated colour frame
        [InlineData(true, true, true, StretchMode.Unlinked)]    // the HOO master
        [InlineData(true, false, false, StretchMode.Unlinked)]  // colour, uncalibrated
        [InlineData(true, false, true, StretchMode.Unlinked)]   // narrowband, uncalibrated: unchanged
        [InlineData(false, true, false, StretchMode.Linked)]    // mono, where the two modes coincide
        [InlineData(false, true, true, StretchMode.Linked)]     // mono narrowband, likewise
        public void AutoResolves(bool isColour, bool calibrated, bool narrowband, StretchMode expected)
            => StretchMode.Auto.ResolveAuto(isColour, calibrated, narrowband).ShouldBe(expected);

        /// <summary>A mode the user picked is never second-guessed, narrowband or not.</summary>
        [Fact]
        public void AnExplicitModeSurvivesTheNewFact()
        {
            StretchMode.Linked.ResolveAuto(isColour: true, calibrationActive: true, isNarrowbandCalibration: true)
                .ShouldBe(StretchMode.Linked);
            StretchMode.Luma.ResolveAuto(isColour: true, calibrationActive: true, isNarrowbandCalibration: true)
                .ShouldBe(StretchMode.Luma);
        }

        /// <summary>
        /// The classifier against the curves that ship, which is where its threshold came from. Measured
        /// over all 183: dual-band filters land at 3 to 8 nm, the tri-band family at 24 to 34, then
        /// nothing until 42 where UHC-style filters start and run into the broadband camera channels
        /// (Nikon R 50 to 60, Johnson U 53, Canon R 68 to 70). The cut is 38 nm, inside that gap.
        /// </summary>
        [Theory]
        [InlineData("OPTOLONG_L_ULTIMATE", true)]     // dual-band, 7.0 nm
        [InlineData("OPTOLONG_L_ENHANCE", true)]      // duo-narrowband, 33.1: the widest that still counts
        [InlineData("IDAS_NBZ", true)]                // dual-band, 24.0
        [InlineData("JOHNSON_U", false)]              // 53.0: the NARROWEST broadband, the case that binds
        [InlineData("BAADER_G", false)]               // 88.0
        [InlineData("BAADER_R", false)]               // 110.0
        [InlineData("OPTOLONG_L_QUAD_ENHANCE", false)] // 206.0, and not the contradiction it reads as: a
                                                       // quad-BAND filter passes four lines, a quad-band
                                                       // ENHANCE passes the continuum between the sodium
                                                       // lines, which is why SPCC has something to measure
        public async Task AShippedCurveIsClassifiedByItsMeasuredWidth(string curveName, bool lineSelective)
        {
            await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

            FilterCurveDatabase.TryGetFilter(curveName, out var curve).ShouldBeTrue(curveName);

            var width = curve.PassbandWidthNm();
            width.ShouldBeGreaterThan(0d, curveName);
            (width <= FilterCurveDatabase.LineSelectiveMaxWidthNm).ShouldBe(lineSelective,
                $"{curveName} measures {width:F1} nm against a {FilterCurveDatabase.LineSelectiveMaxWidthNm} nm cut");
        }

        /// <summary>
        /// A three-channel verdict needs every channel to be line-selective, because that is what the
        /// question means: a system whose green is narrow and whose red is a continuum is a broadband
        /// system with an odd green, and SPCC has something to measure in it.
        /// </summary>
        [Fact]
        public async Task EveryChannelHasToBeLineSelective()
        {
            await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

            FilterCurveDatabase.TryGetFilter("OPTOLONG_L_ULTIMATE", out var narrow).ShouldBeTrue();
            FilterCurveDatabase.TryGetFilter("BAADER_R", out var broad).ShouldBeTrue();

            FilterCurveDatabase.IsLineSelective(narrow, narrow, narrow).ShouldBeTrue();
            FilterCurveDatabase.IsLineSelective(narrow, narrow, broad).ShouldBeFalse();
            FilterCurveDatabase.IsLineSelective(broad, broad, broad).ShouldBeFalse();
        }

        /// <summary>
        /// The wiring, which the truth table above cannot reach: a real document with a calibration on it
        /// has to hand that third fact to Auto. Asserted by comparing Auto's uniforms against the two
        /// concrete modes rather than by reading a flag, because what the report is about is the picture.
        /// </summary>
        [Fact]
        public async Task ADocumentCalibratedThroughANarrowbandFilterRendersUnlinked()
        {
            var document = await AstroImageDocument.AdoptImageAsync(ColourFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "hoo.fits", TestContext.Current.CancellationToken);
            var summary = new ColorCalibrationSummary("SPCC", 1.4f, 1f, 0.7f, StarCount: 120, WhiteReference: "G2V");

            document.InheritColorCalibration((1.4f, 1f, 0.7f), summary, isNarrowband: true);

            var auto = document.ComputeStretchUniforms(StretchMode.Auto, StretchParameters.Default);
            auto.ShouldBe(document.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default));
            auto.ShouldNotBe(document.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default),
                "Linked is what put the cast on screen");
        }

        /// <summary>
        /// The control, and the one that says the change is narrow: the same document, the same triple,
        /// arrived at through a continuum, still renders Linked. Without this the fix could be "Auto never
        /// picks Linked again" and every test above would still pass.
        /// </summary>
        [Fact]
        public async Task ADocumentCalibratedThroughABroadbandFilterStillRendersLinked()
        {
            var document = await AstroImageDocument.AdoptImageAsync(ColourFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "rgb.fits", TestContext.Current.CancellationToken);
            var summary = new ColorCalibrationSummary("SPCC", 1.4f, 1f, 0.7f, StarCount: 120, WhiteReference: "G2V");

            document.InheritColorCalibration((1.4f, 1f, 0.7f), summary, isNarrowband: false);

            var auto = document.ComputeStretchUniforms(StretchMode.Auto, StretchParameters.Default);
            auto.ShouldBe(document.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default));
        }

        /// <summary>How the fit was arrived at travels with the fit, because an enhanced plate is the same
        /// light through the same filter.</summary>
        [Fact]
        public async Task AnEnhancedPlateInheritsHowTheCalibrationWasArrivedAt()
        {
            var original = await AstroImageDocument.AdoptImageAsync(ColourFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "hoo.fits", TestContext.Current.CancellationToken);
            var enhanced = await AstroImageDocument.AdoptImageAsync(ColourFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "hoo_enhanced.fits", TestContext.Current.CancellationToken);
            original.InheritColorCalibration((1.4f, 1f, 0.7f), summary: null, isNarrowband: true);

            enhanced.InheritColorCalibration(original);

            enhanced.IsNarrowbandColorCalibration.ShouldBeTrue();
        }

        /// <summary>Three planes at different levels, so a per-channel curve cannot coincide with a shared
        /// one and the two modes are actually distinguishable.</summary>
        private static Image ColourFrame()
        {
            const int W = 24, H = 16;
            var planes = new float[3][,];
            for (var c = 0; c < 3; c++)
            {
                var plane = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        plane[y, x] = (0.08f * (c + 1)) + (0.01f * ((x + y + c) % 5));
                    }
                }

                planes[c] = plane;
            }

            return new Image([planes[0], planes[1], planes[2]], BitDepth.Float32,
                maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        /// <summary>An empty or dead curve measures zero and is not line-selective, rather than being
        /// infinitely narrow, which is the reading a bare "width &lt;= cut" would give it.</summary>
        [Fact]
        public void ACurveWithNothingInItIsNotNarrowband()
        {
            var empty = default(FilterCurve);
            empty.PassbandWidthNm().ShouldBe(0d);
            FilterCurveDatabase.IsLineSelective(empty, empty, empty).ShouldBeFalse();
        }
    }
}
