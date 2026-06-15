using System;

namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Worm periodic error: a sinusoidal RA offset keyed to the RA worm angle. Positional (the worm
    /// phase IS the PE phase) so it stays correlated with the encoder feature the neural guider reads;
    /// falls back to a wall-clock sine only when no encoder phase is available. This unifies the two
    /// pre-existing PE models (the time-based one on the fake mount and the positional one on the fake
    /// camera) into a single mount-side term.
    /// </summary>
    internal sealed class PeriodicErrorTerm : IDisturbanceTerm
    {
        private readonly double _amplitudeArcsec; // peak amplitude = peak-to-peak / 2
        private readonly double _periodSeconds;

        public PeriodicErrorTerm(double peakToPeakArcsec, double periodSeconds)
        {
            _amplitudeArcsec = peakToPeakArcsec / 2.0;
            _periodSeconds = periodSeconds;
        }

        public DisturbanceStage Stage => DisturbanceStage.Drivetrain;

        public DisturbanceCharacter Character => DisturbanceCharacter.Periodic;

        public double BandwidthHz => _periodSeconds > 0 ? 1.0 / _periodSeconds : 0.0;

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
        {
            if (_amplitudeArcsec <= 0)
            {
                return (0.0, 0.0);
            }

            var phase = double.IsNaN(ctx.RaWormPhaseRadians)
                ? (_periodSeconds > 0 ? 2.0 * Math.PI * ctx.ElapsedSeconds / _periodSeconds : 0.0)
                : ctx.RaWormPhaseRadians;
            return (_amplitudeArcsec * Math.Sin(phase), 0.0);
        }

        public void Reset()
        {
            // Stateless: the contribution is a pure function of the worm phase (or elapsed time).
        }
    }
}
