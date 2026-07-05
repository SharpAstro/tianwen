using Shouldly;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Gemini;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises the pure <see cref="GeminiFocuserProtocol"/> wire codec against an in-memory myFocuserPro2
/// controller (<see cref="FakeGeminiFocuserSerialDevice"/>): get commands round-trip their payload, set
/// commands write the right frame and mutate controller state, and <see cref="GeminiFocuserProtocol.ParsePayload"/>
/// strips the leading status char + trailing terminator.
/// </summary>
public class GeminiFocuserProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Identify_returns_OK_on_a_present_controller()
    {
        var conn = new FakeGeminiFocuserSerialDevice();
        (await GeminiFocuserProtocol.IdentifyAsync(conn, Ct)).ShouldBe(GeminiFocuserProtocol.PresentReply);
        conn.WrittenCommands.ShouldContain(":02#");
    }

    [Fact]
    public async Task Identify_returns_non_OK_when_absent()
    {
        var conn = new FakeGeminiFocuserSerialDevice(present: false);
        (await GeminiFocuserProtocol.IdentifyAsync(conn, Ct)).ShouldNotBe(GeminiFocuserProtocol.PresentReply);
    }

    [Fact]
    public async Task Firmware_reply_splits_name_and_version()
    {
        var conn = new FakeGeminiFocuserSerialDevice(firmwareName: "myFP2ESP32", firmwareVersion: 312);
        var fw = await GeminiFocuserProtocol.GetFirmwareAsync(conn, Ct);

        fw.ShouldNotBeNull();
        fw!.Value.Name.ShouldBe("myFP2ESP32");
        fw.Value.Version.ShouldBe(312);
    }

    [Fact]
    public async Task Position_maxstep_temperature_stepsize_round_trip()
    {
        var conn = new FakeGeminiFocuserSerialDevice(maxStep: 90_000) { Position = 42_345, Temperature = -3.25, StepSize = 4.8 };

        (await GeminiFocuserProtocol.GetPositionAsync(conn, Ct)).ShouldBe(42_345);
        (await GeminiFocuserProtocol.GetMaxStepAsync(conn, Ct)).ShouldBe(90_000);
        (await GeminiFocuserProtocol.GetTemperatureAsync(conn, Ct)).ShouldBe(-3.25, 0.001);
        (await GeminiFocuserProtocol.GetStepSizeAsync(conn, Ct)).ShouldNotBeNull().ShouldBe(4.8, 0.001);
    }

    [Fact]
    public async Task IsMoving_reflects_controller_state()
    {
        var conn = new FakeGeminiFocuserSerialDevice { Moving = false };
        (await GeminiFocuserProtocol.GetIsMovingAsync(conn, Ct)).ShouldBeFalse();

        conn.Moving = true;
        (await GeminiFocuserProtocol.GetIsMovingAsync(conn, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task TempComp_available_and_get_round_trip()
    {
        var conn = new FakeGeminiFocuserSerialDevice(tempCompAvailable: true);
        (await GeminiFocuserProtocol.GetTempCompAvailableAsync(conn, Ct)).ShouldBeTrue();
        (await GeminiFocuserProtocol.GetTempCompAsync(conn, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task SetTempComp_writes_the_toggle_frame_and_updates_state()
    {
        var conn = new FakeGeminiFocuserSerialDevice(tempCompAvailable: true);

        await GeminiFocuserProtocol.SetTempCompAsync(conn, enabled: true, Ct);
        conn.WrittenCommands.ShouldContain(":231#");
        (await GeminiFocuserProtocol.GetTempCompAsync(conn, Ct)).ShouldBeTrue();

        await GeminiFocuserProtocol.SetTempCompAsync(conn, enabled: false, Ct);
        conn.WrittenCommands.ShouldContain(":230#");
        (await GeminiFocuserProtocol.GetTempCompAsync(conn, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Move_writes_the_absolute_target_frame_and_starts_motion()
    {
        var conn = new FakeGeminiFocuserSerialDevice();

        await GeminiFocuserProtocol.MoveAsync(conn, 61_500, Ct);

        conn.WrittenCommands.ShouldContain(":0561500#");
        conn.Target.ShouldBe(61_500);
        conn.Moving.ShouldBeTrue();
    }

    [Fact]
    public async Task Halt_writes_the_halt_frame_and_stops_motion()
    {
        var conn = new FakeGeminiFocuserSerialDevice { Moving = true };

        await GeminiFocuserProtocol.HaltAsync(conn, Ct);

        conn.WrittenCommands.ShouldContain(":27#");
        conn.Moving.ShouldBeFalse();
    }

    [Theory]
    [InlineData("P12345#", "12345")] // leading status char + trailing terminator stripped
    [InlineData("P12345", "12345")]  // terminator already stripped by the read
    [InlineData("!OK", "OK")]
    [InlineData("X", "")]            // bare status char -> empty payload
    public void ParsePayload_strips_status_char_and_terminator(string raw, string expected)
        => GeminiFocuserProtocol.ParsePayload(raw).ShouldBe(expected);

    [Fact]
    public void ParsePayload_returns_null_for_null()
        => GeminiFocuserProtocol.ParsePayload(null).ShouldBeNull();
}
