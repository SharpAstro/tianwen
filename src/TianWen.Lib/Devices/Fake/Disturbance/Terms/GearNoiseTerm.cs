namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Fast mechanical jitter from the gear mesh: a low-amplitude, short-correlation-time
    /// Ornstein-Uhlenbeck process in RA and Dec. Same process shape as wind, but fast enough that a
    /// ~0.5 Hz mount loop cannot track it (see <see cref="BandwidthHz"/>) -- chasing it just injects
    /// noise into the corrections.
    /// </summary>
    internal sealed class GearNoiseTerm : IDisturbanceTerm
    {
        private readonly OrnsteinUhlenbeck2D _process;
        private readonly bool _active;

        public GearNoiseTerm(double amplitudeArcsec = 0.3, double decayTimeSeconds = 0.5, int seed = 17)
        {
            _active = amplitudeArcsec > 0.0 && decayTimeSeconds > 0.0;
            _process = new OrnsteinUhlenbeck2D(amplitudeArcsec, decayTimeSeconds, seed);
        }

        public DisturbanceStage Stage => DisturbanceStage.Drivetrain;

        public DisturbanceCharacter Character => DisturbanceCharacter.Stochastic;

        // Fast jitter, deliberately above the mount's ~0.5 Hz loop so CorrectionActuator.MountPulse
        // cannot null it -- the physical reason gear noise is a guiding noise floor, not a correctable error.
        public double BandwidthHz => 2.0;

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
            => _active ? _process.Advance(ctx.ElapsedSeconds) : (0.0, 0.0);

        public void Reset() => _process.Reset();
    }
}
