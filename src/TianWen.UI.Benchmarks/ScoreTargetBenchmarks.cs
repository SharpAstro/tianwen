using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class ScoreTargetBenchmarks
{
    private Transform _transform = null!;
    private DateTimeOffset _astroDark;
    private DateTimeOffset _astroTwilight;
    private Target[] _targets = null!;

    [Params(10, 20)]
    public byte MinHeight { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Vienna, summer night
        _transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 48.2,
            SiteLongitude = 16.4,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.FromHours(2))
        };

        (_astroDark, _astroTwilight) = ObservationScheduler.CalculateNightWindow(_transform);

        // A mix of targets at different RA/Dec
        _targets =
        [
            new Target(5.588, -5.39, "M42", null),    // Orion Nebula — low from Vienna
            new Target(16.695, 36.46, "M13", null),    // Hercules Cluster — high
            new Target(0.712, 41.27, "M31", null),     // Andromeda — rising
            new Target(13.423, -47.48, "Cen A", null), // never visible from Vienna
            new Target(18.595, 33.03, "M57", null),    // Ring Nebula
            new Target(12.45, 25.99, "Coma Cluster", null),
            new Target(20.69, 42.03, "NGC6992", null), // Veil Nebula
            new Target(3.791, 24.11, "M45", null),     // Pleiades — below horizon in summer
            new Target(23.10, 58.77, "Cas A", null),   // circumpolar from Vienna
            new Target(5.392, -69.75, "LMC", null),    // never visible from Vienna
        ];
    }

    [Benchmark]
    public double ScoreTarget_Single()
    {
        // Score a single well-positioned target
        return (double)ObservationScheduler.ScoreTarget(_targets[1], _transform, _astroDark, _astroTwilight, MinHeight).TotalScore;
    }

    [Benchmark]
    public double ScoreTarget_10Targets()
    {
        var total = 0.0;
        foreach (var target in _targets)
        {
            total += (double)ObservationScheduler.ScoreTarget(target, _transform, _astroDark, _astroTwilight, MinHeight).TotalScore;
        }
        return total;
    }
}
