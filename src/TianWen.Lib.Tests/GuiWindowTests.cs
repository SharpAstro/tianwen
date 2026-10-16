using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The evidence run's window placement (<see cref="GuiWindow"/>) waits on the process it was given and the test's token, never on
/// a clock of its own (review round 1 of #1609, B3).
/// </summary>
public class GuiWindowTests
{
    [Fact(Timeout = 60_000)]
    public async Task APlacementWaitsForTheProcessNotForAClock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a window is placed through user32");
        var ct = TestContext.Current.CancellationToken;
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in (ReadOnlySpan<string>)["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Milliseconds 1500"])
        {
            start.ArgumentList.Add(argument);
        }
        using var windowless = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");

        var placed = await GuiWindow.PlaceOnLeftHalfAsync(windowless, ct);

        placed.ShouldBeNull("the process showed no window");
        windowless.HasExited.ShouldBeTrue("the placement gave up when the process exited, not when a clock ran out");
    }
}
