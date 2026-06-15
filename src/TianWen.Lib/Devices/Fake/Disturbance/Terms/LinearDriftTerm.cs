namespace TianWen.Lib.Devices.Fake.Disturbance.Terms
{
    /// <summary>
    /// A constant-rate drift in both axes, linear in elapsed tracking time. Models a residual
    /// polar-misalignment tracking error as a simplified uniform rate (a real polar error is
    /// HA-dependent; the proper believed-&gt;true tilt transform lives on the FakeSkywatcher). Used by
    /// the simple <c>FakeMountDriver</c> for its <c>PolarDriftRate*</c> stand-in. MountAxis-stage,
    /// quasi-static (DC), so a mount pulse can null it.
    /// </summary>
    internal sealed class LinearDriftTerm(double raArcsecPerSec, double decArcsecPerSec) : IDisturbanceTerm
    {
        private readonly double _raArcsecPerSec = raArcsecPerSec;
        private readonly double _decArcsecPerSec = decArcsecPerSec;

        public DisturbanceStage Stage => DisturbanceStage.MountAxis;

        public DisturbanceCharacter Character => DisturbanceCharacter.Drift;

        public double BandwidthHz => 0.0; // monotonic DC drift

        public (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx)
        {
            if (_raArcsecPerSec == 0.0 && _decArcsecPerSec == 0.0)
            {
                return (0.0, 0.0);
            }

            return (_raArcsecPerSec * ctx.ElapsedSeconds, _decArcsecPerSec * ctx.ElapsedSeconds);
        }

        public void Reset()
        {
            // Stateless: a pure function of elapsed tracking time.
        }
    }
}
