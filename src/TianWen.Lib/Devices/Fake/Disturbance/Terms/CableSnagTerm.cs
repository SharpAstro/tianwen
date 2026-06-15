namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// Cable snag: a one-off positional step in RA and Dec that appears at a set elapsed time and then
    /// persists (the cable catches, yanks the tube, and stays caught until the guider corrects it).
    /// </summary>
    internal sealed class CableSnagTerm(double atSeconds, double raArcsec, double decArcsec) : IDisturbanceTerm
    {
        private readonly double _atSeconds = atSeconds;
        private readonly double _raArcsec = raArcsec;
        private readonly double _decArcsec = decArcsec;

        public DisturbanceStage Stage => DisturbanceStage.OpticalTube;

        public DisturbanceCharacter Character => DisturbanceCharacter.Impulse;

        public double BandwidthHz => 0.0; // a settled step is DC

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
            => _atSeconds > 0.0 && ctx.ElapsedSeconds >= _atSeconds
                ? (_raArcsec, _decArcsec)
                : (0.0, 0.0);

        public void Reset()
        {
            // Stateless: the step is a pure function of elapsed time vs the trigger time.
        }
    }
}
