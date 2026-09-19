using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Lib.Imaging.Stacking;
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
            StretchMode.Linked.ResolveAuto(isColour: true, calibrationActive: true, colourIsNotPhotometric: true)
                .ShouldBe(StretchMode.Linked);
            StretchMode.Luma.ResolveAuto(isColour: true, calibrationActive: true, colourIsNotPhotometric: true)
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
                wcs: null, filePath: "hoo.fits", cancellationToken: TestContext.Current.CancellationToken);
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
                wcs: null, filePath: "rgb.fits", cancellationToken: TestContext.Current.CancellationToken);
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
                wcs: null, filePath: "hoo.fits", cancellationToken: TestContext.Current.CancellationToken);
            var enhanced = await AstroImageDocument.AdoptImageAsync(ColourFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "hoo_enhanced.fits", cancellationToken: TestContext.Current.CancellationToken);
            original.InheritColorCalibration((1.4f, 1f, 0.7f), summary: null, isNarrowband: true);

            enhanced.InheritColorCalibration(original);

            enhanced.IsNarrowbandColorCalibration.ShouldBeTrue();
        }

        /// <summary>
        /// The case the reported file actually is, and the one the filter-curve test above cannot reach.
        /// </summary>
        /// <remarks>
        /// Measured on <c>Sag_Triplet_OIII-HOO_1.fits</c>, an Astro Pixel Processor composite: green and
        /// blue are identical in <b>100.0% of its 9,477,205 pixels</b>, and the header carries no FILTER,
        /// no INSTRUME and no sensor (its filter is in APP's own <c>FILT-1 = 'HOO 1 composite'</c>). So the
        /// throughput route has nothing to read, SPCC never runs, and the calibration on that file comes
        /// from the sky-background path, which needs only stars. The frame itself is what says the colour
        /// is not a measurement: three gains cannot be fitted to two independent channels.
        /// </remarks>
        [Fact]
        public async Task AnHooCompositeRendersUnlinkedEvenWithNoFilterHeader()
        {
            var document = await AstroImageDocument.AdoptImageAsync(HooComposite(), DebayerAlgorithm.None,
                wcs: null, filePath: "Sag_Triplet_OIII-HOO_1.fits", cancellationToken: TestContext.Current.CancellationToken);

            // The sky-background path, which is what a file with no filter or sensor header gets.
            document.InheritColorCalibration((1.4f, 1f, 0.7f), summary: null, isNarrowband: false);

            document.ColourIsNotPhotometric.ShouldBeTrue("green and blue hold the same OIII measurement");

            var auto = document.ComputeStretchUniforms(StretchMode.Auto, StretchParameters.Default);
            auto.ShouldBe(document.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default));
            auto.ShouldNotBe(document.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default));
        }

        /// <summary>
        /// The counting itself, which is the whole basis of the test above: a duplicated plane is not an
        /// independent measurement, and an ordinary frame is unaffected.
        /// </summary>
        [Fact]
        public void ADuplicatedPlaneIsNotAnIndependentChannel()
        {
            HooComposite().IndependentChannelCount().ShouldBe(2);
            ColourFrame().IndependentChannelCount().ShouldBe(3);
            MonoFrame().IndependentChannelCount().ShouldBe(1);
        }

        /// <summary>
        /// An SHO or HOO frame is a THREE-channel frame with two measurements, and an ordinary RGB frame
        /// whose channels merely look alike is not: the test is bit-for-bit equality, so a frame that is
        /// nearly grey still calibrates.
        /// </summary>
        [Fact]
        public async Task ANearlyGreyFrameIsStillPhotometric()
        {
            var document = await AstroImageDocument.AdoptImageAsync(NearlyGreyFrame(), DebayerAlgorithm.None,
                wcs: null, filePath: "grey.fits", cancellationToken: TestContext.Current.CancellationToken);
            document.InheritColorCalibration((1.02f, 1f, 0.99f), summary: null, isNarrowband: false);

            document.ColourIsNotPhotometric.ShouldBeFalse();
            document.ComputeStretchUniforms(StretchMode.Auto, StretchParameters.Default)
                .ShouldBe(document.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default));
        }

        /// <summary>An HOO palette: Ha in red, one OIII plane in BOTH green and blue.</summary>
        private static Image HooComposite()
        {
            const int W = 24, H = 16;
            var ha = new float[H, W];
            var oiii = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    ha[y, x] = 0.10f + (0.01f * ((x + y) % 5));
                    oiii[y, x] = 0.06f + (0.008f * ((x + (2 * y)) % 7));
                }
            }

            return new Image([ha, oiii, oiii], BitDepth.Float32,
                maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        /// <summary>Three planes that are close but not equal, which must NOT read as rank-deficient.</summary>
        private static Image NearlyGreyFrame()
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
                        plane[y, x] = 0.09f + (0.01f * ((x + y) % 5)) + (0.0001f * c);
                    }
                }

                planes[c] = plane;
            }

            return new Image([planes[0], planes[1], planes[2]], BitDepth.Float32,
                maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
        }

        private static Image MonoFrame()
        {
            var plane = new float[16, 24];
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 24; x++)
                {
                    plane[y, x] = 0.1f + (0.01f * ((x + y) % 5));
                }
            }

            return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
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

        /// <summary>
        /// The HEADLESS half of the same rule, which every test above missed because every one of them
        /// went through the viewer. <c>MasterPreviewRenderer</c> is what the dataset bake and
        /// <c>tianwen image render</c> draw with, and it hardcoded <see cref="StretchMode.Linked"/> and
        /// never called <c>ResolveAuto</c>, so a rule that was measured, documented and pinned applied to
        /// exactly one of the two renderers.
        /// </summary>
        /// <remarks>
        /// Found 2026-09-19 chasing teal dual-narrowband gallery cards. <c>tianwen image render</c> on a
        /// real 3 nm Optolong L-Ultimate master printed <c>white-balance 1.135512,1.000000,1.860489
        /// (SPCC)</c>: a photometric fit against a continuum that never reached the sensor, pushing blue up
        /// 86 percent. Rendered Linked, the ONE shared shadow point comes off the mean of three unequal
        /// medians, and red -- the weakest channel after that triple -- fell under it and clipped to zero
        /// once the enhance had shrunk the MAD. The white balance is left exactly as it was; what changes
        /// is that it is no longer ASSERTED as colour.
        /// <para>Asserted on the per-channel curve rather than on <see cref="StretchUniforms.Mode"/>, so a
        /// renderer that set the flag and went on sharing one curve would still fail: Linked writes one
        /// answer into all three slots by construction, Unlinked solves each channel from its own
        /// pixels.</para>
        /// </remarks>
        [Theory]
        [InlineData("Optolong L-Ultimate 3nm", StretchMode.Unlinked)]
        [InlineData("Optolong L-Quad Enhance", StretchMode.Linked)]
        public async Task TheHeadlessRendererHonoursTheSameLineSelectiveVeto(string filterName, StretchMode expected)
        {
            await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

            var master = OscFrame(filterName);
            var renderer = new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance);
            var png = Path.Combine(Path.GetTempPath(), $"tw-{Guid.NewGuid():N}.png");
            try
            {
                // A supplied triple is the gallery's own path (the enhanced render inherits the master's
                // balance) AND the only way to make a calibration active with no catalog in the test, which
                // matters: with none active ResolveAuto answers Unlinked for BOTH rows and the control
                // below would pass against a renderer that had simply stopped picking Linked.
                var render = await renderer.RenderAsync(
                    master, master.ImageMeta, wcs: null, statsSource: null, png,
                    whiteBalanceOverride: (1.136f, 1f, 1.860f),
                    ct: TestContext.Current.CancellationToken);

                render.Uniforms.Mode.ShouldBe(expected, filterName);

                var shadows = render.Uniforms.Shadows;
                var oneCurveForEveryChannel = shadows.R == shadows.G && shadows.G == shadows.B;
                oneCurveForEveryChannel.ShouldBe(expected is StretchMode.Linked,
                    $"{filterName} rendered {render.Uniforms.Mode} with shadows {shadows}");
            }
            finally
            {
                if (File.Exists(png)) File.Delete(png);
            }
        }

        /// <summary>
        /// A debayered OSC master through a named filter, with the three channels at deliberately
        /// different levels so the two modes cannot coincide. <see cref="SensorType.Color"/> is what
        /// brings the CFA curves into <c>BuildChannelThroughputs</c>, and the filter is carried as the
        /// RAW manufacturer name, which is what a real header holds and what the matcher reads.
        /// </summary>
        private static Image OscFrame(string filterName)
        {
            const int W = 64, H = 64;
            var planes = new float[3][,];
            var level = new[] { 0.040f, 0.120f, 0.066f };
            for (var c = 0; c < 3; c++)
            {
                var plane = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        plane[y, x] = level[c] + (0.004f * ((x + y + c) % 7));
                    }
                }

                planes[c] = plane;
            }

            return new Image([planes[0], planes[1], planes[2]], BitDepth.Float32,
                maxValue: 1f, minValue: 0f, pedestal: 0f,
                imageMeta: new ImageMeta
                {
                    Instrument = "SVBONY SV605CC",
                    SensorType = SensorType.Color,
                    // Through Filter.FromName, which is the production path: none of these descriptive
                    // header strings match an anchored pattern, so each canonicalises to Unknown and keeps
                    // its text as RawName. That is also what makes it reach BuildChannelThroughputs at all
                    // -- the optical filter is skipped when FilterNameForFits equals the DisplayName.
                    Filter = Filter.FromName(filterName),
                });
        }
    }
}
