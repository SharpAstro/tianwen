using System;

namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Atmospheric seeing: a zero-mean, per-frame random centroid wander. The only ATMOSPHERE-stage
    /// term, so <see cref="DisturbanceModel.SensorDelta"/> (not <see cref="DisturbanceModel.PointingDelta"/>)
    /// sums it -- it moves the apparent star, not the pointing, so a mount pulse cannot null it and
    /// chasing it just feeds the seeing variance into the corrections. One fresh draw per frame
    /// (white at the exposure cadence); idempotent for repeated reads at the same instant.
    /// </summary>
    internal sealed class AtmosphericSeeingTerm : IDisturbanceTerm
    {
        private readonly double _seeingArcsec;
        private readonly int _seed;

        private Random _rng;
        private double _lastElapsed;
        private double _ra;
        private double _dec;

        public AtmosphericSeeingTerm(double seeingArcsec, int seed = 101)
        {
            _seeingArcsec = seeingArcsec;
            _seed = seed;
            _rng = new Random(seed);
            _lastElapsed = -1.0;
        }

        public DisturbanceStage Stage => DisturbanceStage.Atmosphere;

        public DisturbanceCharacter Character => DisturbanceCharacter.Stochastic;

        // Broadband, fast -- well above any mount loop; only a sensor-side AO actuator could chase it.
        public double BandwidthHz => 10.0;

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
        {
            if (_seeingArcsec <= 0.0)
            {
                return (0.0, 0.0);
            }

            if (ctx.ElapsedSeconds > _lastElapsed)
            {
                _ra = _seeingArcsec * StochasticMath.NextGaussian(_rng);
                _dec = _seeingArcsec * StochasticMath.NextGaussian(_rng);
                _lastElapsed = ctx.ElapsedSeconds;
            }
            return (_ra, _dec);
        }

        public void Reset()
        {
            _rng = new Random(_seed);
            _lastElapsed = -1.0;
            _ra = 0.0;
            _dec = 0.0;
        }
    }
}
