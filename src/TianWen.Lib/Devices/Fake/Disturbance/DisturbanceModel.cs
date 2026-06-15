using System.Collections.Generic;

namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// The single, ordered list of <see cref="IDisturbanceTerm"/> that defines a fake mount/camera's
    /// imperfections. Replaces the three overlapping disturbance implementations (the
    /// <c>FakeMountDriver._accumulated*</c> sums, the <c>FakeSkywatcher</c> misalignment, and the
    /// <c>FakeCamera</c> periodic error) with one model both fakes consume.
    /// </summary>
    /// <remarks>
    /// The split between <see cref="PointingDelta"/> and <see cref="SensorDelta"/> is the believed/true
    /// seam: pointing-stage terms move the optical axis (a mount read or plate-solve witnesses them and
    /// a mount pulse can null them); atmosphere-stage terms move only the apparent centroid (no mount
    /// pulse can null them). The two methods partition the term set, so one full frame evaluates each
    /// term exactly once -- the mount calls <see cref="PointingDelta"/>, the camera calls
    /// <see cref="SensorDelta"/> -- which keeps stochastic terms stepping correctly.
    /// </remarks>
    internal sealed class DisturbanceModel(IReadOnlyList<IDisturbanceTerm> terms)
    {
        private readonly IReadOnlyList<IDisturbanceTerm> _terms = terms;

        /// <summary>The terms composing this model, in declaration order.</summary>
        public IReadOnlyList<IDisturbanceTerm> Terms => _terms;

        /// <summary>
        /// Sum of every pointing-stage term (everything not at <see cref="DisturbanceStage.Atmosphere"/>),
        /// in native-frame arcsec. This is the <c>believed -&gt; true</c> pointing offset the fake mount
        /// adds to its encoder reading.
        /// </summary>
        public (double DRaArcsec, double DDecArcsec) PointingDelta(in DisturbanceContext ctx)
            => Sum(in ctx, atmosphere: false);

        /// <summary>
        /// Sum of every atmosphere-stage term, in native-frame arcsec. This is the centroid-only offset
        /// the fake camera adds AFTER projecting true pointing -- it is not reflected in any pointing read.
        /// </summary>
        public (double DRaArcsec, double DDecArcsec) SensorDelta(in DisturbanceContext ctx)
            => Sum(in ctx, atmosphere: true);

        /// <summary>Resets every term (stochastic state, RNGs, last-evaluated time) for a new session.</summary>
        public void Reset()
        {
            foreach (var term in _terms)
            {
                term.Reset();
            }
        }

        private (double DRaArcsec, double DDecArcsec) Sum(in DisturbanceContext ctx, bool atmosphere)
        {
            var dRa = 0.0;
            var dDec = 0.0;
            foreach (var term in _terms)
            {
                if ((term.Stage == DisturbanceStage.Atmosphere) != atmosphere)
                {
                    continue;
                }

                var (ra, dec) = term.Evaluate(in ctx);
                dRa += ra;
                dDec += dec;
            }
            return (dRa, dDec);
        }
    }
}
