namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// The temporal signature of a disturbance term. Informational metadata that documents how
    /// a term behaves over time; it does not change how <see cref="DisturbanceModel"/> sums terms
    /// (that is driven by <see cref="DisturbanceStage"/> and <see cref="IDisturbanceTerm.BandwidthHz"/>).
    /// </summary>
    internal enum DisturbanceCharacter
    {
        /// <summary>Monotonic, slowly accumulating offset (e.g. flexure, polar-misalignment field drift).</summary>
        Drift,

        /// <summary>Repeating waveform keyed to a phase (e.g. worm periodic error).</summary>
        Periodic,

        /// <summary>A one-off step that persists once triggered (e.g. cable snag).</summary>
        Impulse,

        /// <summary>Time-correlated random process (e.g. wind gusts, gear noise, seeing).</summary>
        Stochastic,
    }
}
