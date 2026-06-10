using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shouldly;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins our SkyWatcher protocol encoding against wire transcripts recorded from
/// GSServer's own GS.SkyWatcher client (the de-facto reference implementation)
/// driving a scripted serial port. Fixture: Data/gss-oracle-transcripts.json,
/// regenerated via tools/GssOracle (requires the GSServer checkout at
/// ../../other/GSServer). Each entry is one command GSS put on the wire, grouped
/// by scenario (init, tracking, pulses, gotos — north and south).
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherGssOracleTests
{
    private sealed record OracleEntry(string Scenario, string Cmd, string Reply);

    private static readonly Lazy<IReadOnlyList<OracleEntry>> _entries = new(LoadTranscript);

    private static IReadOnlyList<OracleEntry> LoadTranscript()
    {
        var assembly = typeof(SkywatcherGssOracleTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("gss-oracle-transcripts.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("gss-oracle-transcripts.json fixture is not embedded");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("fixture stream was null");
        using var doc = JsonDocument.Parse(stream);
        var list = new List<OracleEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            list.Add(new OracleEntry(
                item.GetProperty("scenario").GetString()!,
                item.GetProperty("cmd").GetString()!,
                item.GetProperty("reply").GetString()!));
        }
        return list;
    }

    private static IEnumerable<OracleEntry> Scenario(string scenario)
        => _entries.Value.Where(e => e.Scenario == scenario);

    [Fact]
    public void EveryGssMotionModePayload_RoundtripsThroughOurCodec()
    {
        var gCommands = _entries.Value.Where(e => e.Cmd.StartsWith(":G", StringComparison.Ordinal)).ToList();
        gCommands.ShouldNotBeEmpty();
        foreach (var entry in gCommands)
        {
            var payload = entry.Cmd[3..]; // strip ":G<axis>"
            SkywatcherProtocol.TryDecodeMotionMode(payload.AsSpan(), out var func, out var forward, out var south)
                .ShouldBeTrue($"GSS payload '{payload}' in {entry.Scenario} must decode");
            SkywatcherProtocol.EncodeMotionMode(func, forward, south)
                .ShouldBe(payload, $"re-encode of GSS payload in {entry.Scenario}");
        }
    }

    [Theory]
    // Tracking: forward low-speed slew in the north; reverse + hemisphere bit in the south.
    [InlineData("north-track-sidereal", ":G110")]
    [InlineData("south-track-sidereal", ":G113")]
    // Dec rate-mode pulses: low-speed slew, direction by pulse sign, south bit below the equator.
    [InlineData("north-pulse-dec-north-f05", ":G210")]
    [InlineData("north-pulse-dec-south-f05", ":G211")]
    [InlineData("south-pulse-dec-north-f05", ":G212")]
    [InlineData("south-pulse-dec-south-f05", ":G213")]
    // Dec micro-GOTO pulse: low-speed goto.
    [InlineData("north-pulse-dec-goto-north-f05", ":G220")]
    [InlineData("south-pulse-dec-goto-north-f05", ":G222")]
    // GOTO: high-speed for long slews, low-speed within the goto margin.
    [InlineData("north-goto-axis1-45deg", ":G100")]
    [InlineData("north-movesteps-axis1", ":G120")]
    [InlineData("south-movesteps-axis1", ":G122")]
    // MoveAxis-style fast slew: high-speed slew func.
    [InlineData("north-slew-fast-3degsec", ":G130")]
    [InlineData("south-slew-fast-3degsec", ":G132")]
    public void GssScenario_UsesExpectedMotionMode(string scenario, string expectedGCommand)
    {
        Scenario(scenario).Select(e => e.Cmd)
            .ShouldContain(expectedGCommand, $"scenario {scenario}");
    }

    [Theory]
    [InlineData("north-pulse-ra-west-f05")]
    [InlineData("north-pulse-ra-east-f05")]
    [InlineData("north-pulse-ra-east-f10")] // f=1.0 east: GSS commands sidereal/1000, still :I-only
    [InlineData("south-pulse-ra-west-f05")]
    [InlineData("south-pulse-ra-east-f05")]
    public void GssRaPulse_SameDirection_IsLiveRateChangeOnly(string scenario)
    {
        // GSS pulses RA by changing the step period (:I) while the axis keeps
        // running — no stop/start, no motion-mode change. This is the contract
        // PulseGuideAsync must follow (fix-sw item 3).
        var cmds = Scenario(scenario).Select(e => e.Cmd[..2]).Distinct().ToList();
        cmds.ShouldNotBeEmpty();
        cmds.ShouldAllBe(prefix => prefix == ":I" || prefix == ":f" || prefix == ":j",
            $"scenario {scenario} must only query status and adjust :I step period");
    }

    [Fact]
    public void GssGoto_SendsBreakPointIncrement_BeforeStart()
    {
        // GSS sends :H (target increment) then :M (break-point steps: 3500 high-speed,
        // 0 low-speed) then :J. Our driver must do the same once :M support lands.
        var goto45 = Scenario("north-goto-axis1-45deg").Select(e => e.Cmd).ToList();
        goto45.ShouldContain(":H1703611");
        goto45.ShouldContain(":M1AC0D00"); // 3500 = 0x0DAC, LE "AC0D00"
        var hIdx = goto45.FindIndex(c => c.StartsWith(":H1", StringComparison.Ordinal));
        var mIdx = goto45.FindIndex(c => c.StartsWith(":M1", StringComparison.Ordinal));
        var jIdx = goto45.FindIndex(c => c == ":J1");
        hIdx.ShouldBeLessThan(mIdx);
        mIdx.ShouldBeLessThan(jIdx);
    }

    [Fact]
    public void GssSiderealTrackingPreset_MatchesOurT1Computation()
    {
        // north-track-sidereal commanded :I1F23700 = T1 0x0037F2 = 14322 (EQ6: CPR
        // 9024000, timer 1500000). GSS truncates where we round — allow ±1 tick.
        var iCmd = Scenario("north-track-sidereal").Select(e => e.Cmd)
            .Single(c => c.StartsWith(":I1", StringComparison.Ordinal));
        var gssT1 = SkywatcherProtocol.DecodeUInt24(iCmd.AsSpan(3, 6));

        var siderealDegPerSec = 15.041067 / 3600.0;
        var ourT1 = SkywatcherProtocol.ComputeT1Preset(1500000, 9024000, siderealDegPerSec, false, 16);
        ((double)ourT1).ShouldBe(gssT1, 1.0);
    }
}
