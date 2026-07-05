using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises <see cref="GeminiFocuserSerialProbe"/> against an in-memory myFocuserPro2 controller: a valid
/// <c>:02#</c> handshake produces a <see cref="Discovery.SerialProbeMatch"/> with the port + firmware baked
/// into the URI/metadata, a non-present controller produces no match, and the probe advertises the right
/// device host + framing.
/// </summary>
public class GeminiFocuserSerialProbeTests
{
    [Fact]
    public async Task Valid_handshake_produces_a_match_with_port_and_firmware()
    {
        var conn = new FakeGeminiFocuserSerialDevice(firmwareName: "myFP2ESP32", firmwareVersion: 312);
        var probe = new GeminiFocuserSerialProbe();

        var match = await probe.ProbeAsync("serial:COM5", conn, TestContext.Current.CancellationToken);

        match.ShouldNotBeNull();
        match.Port.ShouldBe("serial:COM5");
        // System.Uri lower-cases scheme + host.
        match.DeviceUri.Scheme.ShouldBe("focuser");
        match.DeviceUri.Host.ShouldBe(nameof(GeminiFocuserDevice).ToLowerInvariant());
        match.DeviceUri.QueryValue(DeviceQueryKey.Port).ShouldBe("serial:COM5");
        match.Metadata.ShouldNotBeNull();
        match.Metadata!["firmwareName"].ShouldBe("myFP2ESP32");
        match.Metadata!["firmware"].ShouldBe("312");
    }

    [Fact]
    public async Task Absent_controller_produces_no_match()
    {
        var conn = new FakeGeminiFocuserSerialDevice(present: false);
        var probe = new GeminiFocuserSerialProbe();

        var match = await probe.ProbeAsync("serial:COM5", conn, TestContext.Current.CancellationToken);

        match.ShouldBeNull();
    }

    [Fact]
    public void Probe_advertises_the_Gemini_focuser_device_host_and_hash_framing()
    {
        var probe = new GeminiFocuserSerialProbe();
        probe.MatchesDeviceHosts.ShouldContain(nameof(GeminiFocuserDevice));
        probe.Framing.ShouldBe(ProbeFraming.HashTerminated);
        probe.BaudRate.ShouldBe(GeminiFocuserProtocol.Baud);
        probe.AssertControlLines.ShouldBeTrue();
    }
}
