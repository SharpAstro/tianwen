using Shouldly;
using TianWen.Lib.Devices.Discovery;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Covers the normalisation used by <see cref="IPinnedSerialPortsProvider"/>
/// implementations: profile URIs may store any of a dozen forms for <c>?port=</c>,
/// but the filter needs a single canonical form to compare against the enumerated
/// <c>serial:…</c> port list.
/// </summary>
public class SerialPortNamesTests
{
    [Theory]
    [InlineData("serial:COM5", "serial:COM5")]    // already prefixed
    [InlineData("COM5", "serial:COM5")]            // Windows name
    [InlineData("com12", "serial:com12")]          // case-insensitive
    [InlineData("ttyUSB0", "serial:ttyUSB0")]      // bare tty
    [InlineData("/dev/ttyUSB0", "serial:/dev/ttyUSB0")]  // Unix path
    public void RecognisedFormsNormaliseToSerialPrefix(string raw, string expected)
    {
        SerialPortNames.TryNormalize(raw, out var normalized).ShouldBeTrue(raw);
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wifi")]        // Canon sentinel
    [InlineData("wpd")]         // Canon sentinel
    [InlineData("SkyWatcher")]  // fake-device sentinel
    [InlineData("LX200")]       // fake-device sentinel
    [InlineData("COM")]         // too short — not a real port
    [InlineData("random-garbage")]
    public void UnrecognisedFormsReturnFalse(string? raw)
    {
        SerialPortNames.TryNormalize(raw, out var normalized).ShouldBeFalse(raw ?? "<null>");
        normalized.ShouldBeEmpty();
    }
}
