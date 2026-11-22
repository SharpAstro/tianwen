using System;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Devices.Ascom.ComInterop;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the node does with a line its ASCOM helper printed on stderr (#1811, #2426). The helper has no logger, so its
/// crash handler writes there, and the node drains the pipe into its own log. The drain needs the built helper
/// (<c>AscomHostProcessTests</c>, in the simulator suite); what one line becomes does not.
/// </summary>
[SupportedOSPlatform("Windows")]
public class AscomHostStderrLineTests
{
    private const string WindowsOnly = "The ASCOM helper is Windows only.";

    [Fact]
    public void ALineIsLoggedAsAnErrorUnderTheHelpersName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var logger = new RecordingLogger();

        AscomHostProcess.LogHelperStderrLine(logger, "Unhandled exception, the process is ending: System.InvalidOperationException: boom");

        logger.Entries.ShouldBe(
            [(LogLevel.Error, null, "tianwen-ascomhost: Unhandled exception, the process is ending: System.InvalidOperationException: boom")]);
    }

    [Theory]
    [InlineData(null)] // the end of the stream
    [InlineData("")]
    [InlineData("   ")]
    public void TheEndOfTheStreamAndABlankLineAreNotLogged(string? line)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var logger = new RecordingLogger();

        AscomHostProcess.LogHelperStderrLine(logger, line);

        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void WithNoLoggerALineIsDroppedAndNothingThrows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        Should.NotThrow(() => AscomHostProcess.LogHelperStderrLine(null, "a line nobody reads"));
    }
}
