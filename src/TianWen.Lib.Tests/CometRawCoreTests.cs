using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins <see cref="CometRawCore"/>: the raw frames' central window, comet-aligned and
    /// median-stacked, lands the body on the centre cell in every colour and lets a fixed star trail
    /// out of the median. The failure it guards is a basis slip: a window taken around the wrong point
    /// stacks sky, and the splice then relates sky to the model and calls it a nucleus.
    /// </summary>
    public class CometRawCoreTests
    {
        private static readonly DateTimeOffset Epoch = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        private static readonly float[] Peaks = [0.10f, 0.30f, 0.18f];
        private static readonly int[,] Rggb = { { 0, 1 }, { 1, 2 } };

        private static float Coma(float r) => 1f / (1f + (r / 15f) * (r / 15f));

        [Fact]
        public async Task TheBodyLandsOnTheCentreCellAndAFixedStarTrailsOutOfTheMedian()
        {
            var dir = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", "comet-raw-core", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rate = new Vector2(10f, 0f);              // px/h on the reference grid
                var bodyOnGrid = new Vector2(150.4f, 149.7f);  // sub-pixel, on purpose
                var starRef = bodyOnGrid + new Vector2(25f, 10f); // fixed on the sky, inside the window
                const float amplitude = 2000f;
                float[] sky = [1000f, 1400f, 1200f];
                var reference = new ImageMeta
                {
                    Instrument = "synth",
                    ExposureStartTime = Epoch,
                    ExposureDuration = TimeSpan.FromSeconds(60),
                    FrameType = FrameType.Light,
                    SensorType = SensorType.RGGB,
                };
                var rng = new Random(31);
                var frames = new List<(FrameInfo Light, Matrix3x2 StarTransform)>();
                for (var i = 0; i < 36; i++)
                {
                    var start = Epoch.AddMinutes(10 * i);
                    var dtHours = (start - Epoch).TotalHours;
                    // Dither: frame -> reference is a pure translation by d.
                    var d = new Vector2((float)(rng.NextDouble() * 30 - 15), (float)(rng.NextDouble() * 30 - 15));
                    var starSolution = Matrix3x2.CreateTranslation(d);
                    // Where the body is on the reference grid at t_i, and hence in this frame.
                    var bodyRef = bodyOnGrid + rate * (float)dtHours;
                    var bodyInFrame = bodyRef - d;
                    var starInFrame = starRef - d;

                    var plane = new float[300, 300];
                    for (var y = 0; y < 300; y++)
                    {
                        for (var x = 0; x < 300; x++)
                        {
                            var c = Rggb[y & 1, x & 1];
                            var r = Vector2.Distance(new Vector2(x, y), bodyInFrame);
                            var s2 = Vector2.DistanceSquared(new Vector2(x, y), starInFrame);
                            plane[y, x] = sky[c] + amplitude * Peaks[c] * Coma(r)
                                + 20000f * MathF.Exp(-s2 / (2f * 1.5f * 1.5f))
                                + (float)(rng.NextDouble() * 6 - 3);
                        }
                    }
                    var meta = reference with { ExposureStartTime = start };
                    var image = new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: meta);
                    var path = Path.Combine(dir, $"frame_{i:D2}.fits");
                    image.WriteToFitsFile(path);
                    Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
                    info.ShouldNotBeNull();
                    info.Meta.ExposureStartTime.ShouldBe(start);
                    frames.Add((info, starSolution));
                }

                var core = await CometRawCore.StackAsync(
                    frames, rate, reference, bodyOnGrid, channels: 3, radiusPx: 40,
                    new Calibrator(null, null, null), NullLogger.Instance, CancellationToken.None);

                core.ShouldNotBeNull();
                core.Length.ShouldBe(3);
                for (var c = 0; c < 3; c++)
                {
                    var plane = core[c];
                    plane.GetLength(0).ShouldBe(81);
                    // The body is on the centre cell: full coma, every colour, no star.
                    plane[40, 40].ShouldBe(sky[c] + amplitude * Peaks[c], amplitude * Peaks[c] * 0.08f);
                    // 20 px out the coma is what the profile says, in whatever direction.
                    plane[40, 60].ShouldBe(sky[c] + amplitude * Peaks[c] * Coma(20f), amplitude * Peaks[c] * 0.08f + 6f);
                    plane[20, 40].ShouldBe(sky[c] + amplitude * Peaks[c] * Coma(20f), amplitude * Peaks[c] * 0.08f + 6f);
                    // The fixed star sweeps the row 10 px below the body, from +25 to -33 px, and
                    // occupies any one cell in a frame or two of 36: the median never sees it.
                    for (var dx = -12; dx <= 24; dx += 4)
                    {
                        var expected = sky[c] + amplitude * Peaks[c] * Coma(MathF.Sqrt(dx * dx + 100f));
                        plane[50, 40 + dx].ShouldBe(expected, amplitude * Peaks[c] * 0.10f + 6f);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp hygiene */ }
            }
        }
    }
}
