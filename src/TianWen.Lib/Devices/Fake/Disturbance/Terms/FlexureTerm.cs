namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Differential flexure: a slow Dec drift proportional to the hour angle the mount has tracked
    /// through (the optical tube sags more as it swings). Hour angle advances at the sidereal rate, so
    /// the drift is linear in elapsed tracking time.
    /// </summary>
    internal sealed class FlexureTerm(double decArcsecPerHaHour) : IDisturbanceTerm
    {
        // Hour angle advances one full turn (24 h) per sidereal day.
        private const double SiderealHoursPerSecond = 24.0 / 86164.0905;

        private readonly double _decArcsecPerHaHour = decArcsecPerHaHour;

        public DisturbanceStage Stage => DisturbanceStage.OpticalTube;

        public DisturbanceCharacter Character => DisturbanceCharacter.Drift;

        public double BandwidthHz => 0.0; // monotonic DC drift

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
        {
            if (_decArcsecPerHaHour == 0.0)
            {
                return (0.0, 0.0);
            }

            var haHours = ctx.ElapsedSeconds * SiderealHoursPerSecond;
            return (0.0, _decArcsecPerHaHour * haHours);
        }

        public void Reset()
        {
            // Stateless: a pure function of elapsed tracking time.
        }
    }
}
