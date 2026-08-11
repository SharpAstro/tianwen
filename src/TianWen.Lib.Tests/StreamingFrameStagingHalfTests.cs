using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the saturating float32 -> <see cref="Half"/> conversion in
    /// <see cref="StreamingFrameStaging.WriteHalf"/>.
    ///
    /// <para><b>The bug this must keep dead</b> (found 2026-08-11 when a retained dataset master
    /// refused to open in ASTAP with "invalid floating point operation"): a bare <c>(Half)</c> cast
    /// maps any float at or above 65,520 to +Inf, and calibrated frames legitimately get there -- a
    /// N.I.N.A. 16-bit light peaks at 65,532 before calibration, and flat division at a vignetted
    /// saturated star core pushes higher. Staged +Inf samples then integrate into the session master
    /// as Inf/NaN at exactly the star cores, the master's MaxValue reads +Inf, the tile pre-stretch
    /// divides by it, and every master tile of the session quantises to zero. Measured blast radius
    /// on the real archive: 5 of 50 sessions, 1,500 all-zero "truth" tiles, and the zero-skew parity
    /// gate green throughout, because a stored zero compares equal to a re-derived zero. The gate can
    /// catch drift, never emptiness, which is why the conversion itself has to be safe.</para>
    /// </summary>
    [Collection("Imaging")]
    public class StreamingFrameStagingHalfTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "stg-" + Guid.NewGuid().ToString("N")[..8]);

        public StreamingFrameStagingHalfTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static Image MakeImage(float[,] data)
        {
            var meta = new ImageMeta("stg-test", DateTime.UtcNow, TimeSpan.FromSeconds(1),
                FrameType.Light, "", 3.76f, 3.76f, 100, -1, Filter.None, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
            return new Image([data], BitDepth.Float32, 70000f, 0f, 0, meta);
        }

        [Fact]
        public void WriteHalf_SaturatesOverflow_KeepsNaN_RoundTripsFiniteValues()
        {
            var halfMax = (float)Half.MaxValue; // 65504
            // One 8-wide row exercising every class the conversion must handle:
            // overflow (+/-), the exact ceiling, a saturated N.I.N.A. light peak (65,532: the
            // real-world value that triggered the bug), NaN coverage border, zero, and two
            // ordinary values that must survive to Half precision.
            var data = new float[,]
            {
                { 70000f, -70000f, halfMax, 65532f, float.NaN, 0f, 1234f, 811.5f },
            };

            var path = Path.Combine(_dir, "frame.f16stage");
            StreamingFrameStaging.WriteHalf(MakeImage(data), path);

            using var reader = new StreamingFrameReader(path);
            Span<float> row = stackalloc float[8];
            reader.ReadStripe(channel: 0, rowStart: 0, rowCount: 1, row);

            // The three members that used to overflow to +/-Inf all land AT the ceiling: a
            // saturated pixel's honest value is "as bright as the container can say", since the
            // sensor never knew the true flux either.
            row[0].ShouldBe(halfMax);
            row[1].ShouldBe(-halfMax);
            row[2].ShouldBe(halfMax);
            row[3].ShouldBe(halfMax);

            // NaN is the warp's no-coverage marker and must pass through untouched; mapping it to
            // the ceiling would turn "no data here" into "very bright here" along every border.
            float.IsNaN(row[4]).ShouldBeTrue();

            row[5].ShouldBe(0f);
            row[6].ShouldBe(1234f);        // exactly representable in Half
            row[7].ShouldBe(811.5f, 0.5f); // within Half's ~10-bit mantissa at this magnitude

            // The invariant the whole dataset pipeline needs, stated directly: nothing that goes
            // in finite comes back non-finite.
            for (var i = 0; i < row.Length; i++)
            {
                if (!float.IsNaN(data[0, i]))
                {
                    float.IsFinite(row[i]).ShouldBeTrue($"index {i}: finite in, non-finite out");
                }
            }
        }
    }
}
