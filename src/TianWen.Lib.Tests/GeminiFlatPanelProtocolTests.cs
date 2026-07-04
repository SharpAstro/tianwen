using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Wire-level tests for the Gemini FlatPanel Lite codec against an in-memory
/// <see cref="FakeGeminiFlatPanelSerialDevice"/> — the '&gt; … #' framing, the H/V/S/J queries, and the
/// L/D/B actions. No hardware.
/// </summary>
public class GeminiFlatPanelProtocolTests
{
    [Fact]
    public async Task Identify_returns_the_identity_payload()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice();
        var id = await GeminiFlatPanelProtocol.IdentifyAsync(conn, TestContext.Current.CancellationToken);
        id.ShouldBe(GeminiFlatPanelProtocol.Identity);
        conn.WrittenCommands.ShouldContain(">H#");
    }

    [Fact]
    public async Task GetFirmwareVersion_parses_the_V_reply()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice(firmware: 205);
        (await GeminiFlatPanelProtocol.GetFirmwareVersionAsync(conn, TestContext.Current.CancellationToken)).ShouldBe(205);
    }

    [Fact]
    public async Task CalibratorState_reflects_light_on_off()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice();
        var ct = TestContext.Current.CancellationToken;

        (await GeminiFlatPanelProtocol.GetCalibratorStateAsync(conn, ct)).ShouldBe(CalibratorStatus.Off);

        await GeminiFlatPanelProtocol.SetLightAsync(conn, on: true, ct);
        conn.WrittenCommands.ShouldContain(">L#");
        (await GeminiFlatPanelProtocol.GetCalibratorStateAsync(conn, ct)).ShouldBe(CalibratorStatus.Ready);

        await GeminiFlatPanelProtocol.SetLightAsync(conn, on: false, ct);
        conn.WrittenCommands.ShouldContain(">D#");
        (await GeminiFlatPanelProtocol.GetCalibratorStateAsync(conn, ct)).ShouldBe(CalibratorStatus.Off);
    }

    [Fact]
    public async Task SetBrightness_writes_B_command_and_round_trips_via_J()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice();
        var ct = TestContext.Current.CancellationToken;

        (await GeminiFlatPanelProtocol.GetBrightnessAsync(conn, ct)).ShouldBe(0);

        await GeminiFlatPanelProtocol.SetBrightnessAsync(conn, 128, ct);
        conn.WrittenCommands.ShouldContain(">B128#");
        (await GeminiFlatPanelProtocol.GetBrightnessAsync(conn, ct)).ShouldBe(128);
    }

    [Fact]
    public async Task SetBrightness_clamps_to_max()
    {
        var conn = new FakeGeminiFlatPanelSerialDevice();
        var ct = TestContext.Current.CancellationToken;

        await GeminiFlatPanelProtocol.SetBrightnessAsync(conn, 999, ct);
        conn.WrittenCommands.ShouldContain(">B255#");
        (await GeminiFlatPanelProtocol.GetBrightnessAsync(conn, ct)).ShouldBe(255);
    }

    [Theory]
    [InlineData("*HGeminiFlatPanelLite#", 'H', "GeminiFlatPanelLite")] // real hardware '*' response sigil
    [InlineData("*V205#", 'V', "205")]
    [InlineData("*S100#", 'S', "100")]                                 // real on-status payload
    [InlineData(">HGeminiFlatPanelLite#", 'H', "GeminiFlatPanelLite")] // full frame, legacy '>' sigil still parses
    [InlineData("HGeminiFlatPanelLite", 'H', "GeminiFlatPanelLite")]   // terminator + prefix already stripped
    [InlineData(">V205#", 'V', "205")]
    [InlineData(">S1#", 'S', "1")]
    [InlineData("*V205#", 'H', null)]                                  // wrong echoed letter
    [InlineData(null, 'H', null)]                                      // no reply
    public void ParsePayload_extracts_the_payload_when_the_letter_matches(string? raw, char expected, string? payload)
    {
        GeminiFlatPanelProtocol.ParsePayload(raw, expected).ShouldBe(payload);
    }
}
