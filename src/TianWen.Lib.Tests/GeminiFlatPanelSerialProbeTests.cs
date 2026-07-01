using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises <see cref="GeminiFlatPanelSerialProbe"/> against an in-memory Gemini controller: a valid
/// <c>&gt;H#</c> handshake produces a <see cref="Discovery.SerialProbeMatch"/> with the port + firmware baked
/// into the URI, a foreign identity produces no match, and the probe advertises the right device host.
/// </summary>
public class GeminiFlatPanelSerialProbeTests
{
    [Fact]
    public async Task Valid_handshake_produces_a_match_with_port_and_firmware()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice(firmware: 205);
        var probe = new GeminiFlatPanelSerialProbe();

        var match = await probe.ProbeAsync("serial:COM4", conn, TestContext.Current.CancellationToken);

        match.ShouldNotBeNull();
        match.Port.ShouldBe("serial:COM4");
        // System.Uri lower-cases scheme + host.
        match.DeviceUri.Scheme.ShouldBe("covercalibrator");
        match.DeviceUri.Host.ShouldBe(nameof(GeminiDevice).ToLowerInvariant());
        match.DeviceUri.QueryValue(DeviceQueryKey.Port).ShouldBe("serial:COM4");
        match.Metadata.ShouldNotBeNull();
        match.Metadata!["firmware"].ShouldBe("205");
    }

    [Fact]
    public async Task Foreign_identity_produces_no_match()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice(identity: "SomeOtherPanel");
        var probe = new GeminiFlatPanelSerialProbe();

        var match = await probe.ProbeAsync("serial:COM4", conn, TestContext.Current.CancellationToken);

        match.ShouldBeNull();
    }

    [Fact]
    public void Probe_advertises_the_Gemini_device_host_and_hash_framing()
    {
        var probe = new GeminiFlatPanelSerialProbe();
        probe.MatchesDeviceHosts.ShouldContain(nameof(GeminiDevice));
        probe.Framing.ShouldBe(ProbeFraming.HashTerminated);
        probe.BaudRate.ShouldBe(GeminiFlatPanelProtocol.Baud);
    }
}
