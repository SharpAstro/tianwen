namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Wind gusting on the optical tube: a slow, large-scale time-correlated push in RA and Dec,
    /// modelled as a 2-D Ornstein-Uhlenbeck process. Slow enough that a mount can partially follow it,
    /// but its random walk leaves a residual the guider cannot fully null.
    /// </summary>
    internal sealed class WindGustTerm : IDisturbanceTerm
    {
        private readonly OrnsteinUhlenbeck2D _process;
        private readonly bool _active;

        public WindGustTerm(double amplitudeArcsec, double decayTimeSeconds, int seed = 23)
        {
            _active = amplitudeArcsec > 0.0 && decayTimeSeconds > 0.0;
            _process = new OrnsteinUhlenbeck2D(amplitudeArcsec, decayTimeSeconds, seed);
        }

        public DisturbanceStage Stage => DisturbanceStage.OpticalTube;

        public DisturbanceCharacter Character => DisturbanceCharacter.Stochastic;

        // Slow, large-scale gusting -- below the ~0.5 Hz mount loop, so nominally trackable.
        public double BandwidthHz => 0.2;

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
            => _active ? _process.Advance(ctx.ElapsedSeconds) : (0.0, 0.0);

        public void Reset() => _process.Reset();
    }
}
