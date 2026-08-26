using System;
using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A NaN in the sample column must not switch rejection off.
    ///
    /// <para>This is the shape of the bug it pins, and it is worth stating because nothing about it
    /// is visible from the outside: every comparison against NaN is false, so quickselect returns
    /// nonsense, the MAD comes out NaN, the <c>mad &lt;= 0</c> degenerate guard does not fire (also
    /// false for NaN), both bounds become NaN, and <c>v &lt; NaN</c> / <c>v &gt; NaN</c> are both
    /// false. No sample is rejected, the loop breaks on its first pass, nothing throws and nothing
    /// logs.</para>
    ///
    /// <para>It has always affected canvas edges, where warped frames leave NaN borders. It became
    /// visible when <c>CometMask</c> put NaN in the middle of the frame and hot pixels survived
    /// there as clumps while being clipped everywhere else: rejection rate 0.0000 inside the masked
    /// band against 0.026-0.034 outside, measured on C/2025 R2.</para>
    /// </summary>
    public class RejectorAbsentSampleTests
    {
        /// <summary>Twelve clean samples near 100, one obvious cosmic-ray hit at 900.</summary>
        private static float[] Column(bool withAbsent)
        {
            var v = new float[]
            {
                100f, 101f, 99f, 100.5f, 99.5f, 100.2f,
                99.8f, 100.1f, 99.9f, 100.3f, 99.7f, 900f,
            };
            if (!withAbsent)
            {
                return v;
            }
            var w = new float[v.Length + 4];
            Array.Copy(v, w, v.Length);
            for (var i = v.Length; i < w.Length; i++)
            {
                w[i] = float.NaN;
            }
            return w;
        }

        public static TheoryData<string, IPixelRejector> Rejectors() => new()
        {
            { "sigma", new SigmaClipRejector(3f, 3f) },
            { "winsorized", new WinsorizedSigmaClipRejector(3f, 3f) },
            { "linearfit", new LinearFitClipRejector() },
            { "percentile", new PercentileClipRejector(0.1f, 0.1f) },
            { "minmax", new MinMaxClipRejector(1, 1) },
        };

        [Theory]
        [MemberData(nameof(Rejectors))]
        public void AnOutlierIsStillRejectedWhenSomeSamplesAreAbsent(string name, IPixelRejector rejector)
        {
            var clean = Column(withAbsent: false);
            var mask = new float[clean.Length];
            rejector.Reject(clean, mask);
            mask[11].ShouldBe(0f, $"{name}: the 900 outlier must be rejected with no NaN present");

            var withNaN = Column(withAbsent: true);
            var mask2 = new float[withNaN.Length];
            rejector.Reject(withNaN, mask2);
            mask2[11].ShouldBe(0f,
                $"{name}: the SAME outlier must still be rejected when four samples are absent. "
                    + "If this reads 1, NaN reached the statistics and rejection is off for the pixel.");
        }

        [Theory]
        [MemberData(nameof(Rejectors))]
        public void AbsentSamplesAreMarkedNotKeptSoTheyCannotReachTheCombine(string name, IPixelRejector rejector)
        {
            var col = Column(withAbsent: true);
            var mask = new float[col.Length];
            rejector.Reject(col, mask);
            for (var i = 12; i < col.Length; i++)
            {
                mask[i].ShouldBe(0f, $"{name}: index {i} is NaN and must not be marked kept");
            }
        }

        [Theory]
        [MemberData(nameof(Rejectors))]
        public void AnAbsentSampleIsNotCountedAsARejection(string name, IPixelRejector rejector)
        {
            // The return feeds the rejection MAP. A frame that simply does not overlap here was never
            // a candidate, so counting it would paint the canvas edges -- and any masked region --
            // as heavily rejected when nothing was rejected at all.
            var col = Column(withAbsent: true);
            var mask = new float[col.Length];
            var notRejected = rejector.Reject(col, mask);
            var rejections = col.Length - notRejected;
            rejections.ShouldBeLessThanOrEqualTo(4,
                $"{name}: only real outliers count; 4 absent samples must not read as rejections");
            rejections.ShouldBeGreaterThan(0, $"{name}: the 900 outlier is a real rejection");
        }

        [Fact]
        public void AColumnThatIsEntirelyAbsentRejectsNothingAndDoesNotThrow()
        {
            // Outside every frame's footprint. Degenerate, common at canvas corners, and it must be
            // uneventful rather than an exception on a 9-million-pixel loop.
            var col = new float[8];
            Array.Fill(col, float.NaN);
            foreach (var (_, rejector) in new (string, IPixelRejector)[]
            {
                ("sigma", new SigmaClipRejector()),
                ("winsorized", new WinsorizedSigmaClipRejector()),
                ("linearfit", new LinearFitClipRejector()),
                ("percentile", new PercentileClipRejector(0.1f, 0.1f)),
                ("minmax", new MinMaxClipRejector(1, 1)),
            })
            {
                var mask = new float[col.Length];
                var notRejected = rejector.Reject(col, mask);
                notRejected.ShouldBe(col.Length);
                foreach (var m in mask)
                {
                    m.ShouldBe(0f);
                }
            }
        }
    }
}
