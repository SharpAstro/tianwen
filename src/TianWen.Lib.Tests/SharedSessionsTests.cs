using Shouldly;
using TianWen.DAL;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The DAL's counted open (<see cref="SharedSessions{TKey}"/>), which every native binding's <c>Open</c> and <c>Close</c>
/// promise: an enumeration or a second connect opens and closes a device a driver may hold, and must never end the
/// driver's session (an ASI462MC streaming at 92 frames a second failed CameraClosed on a device listing, 2026-09-28).
/// </summary>
public class SharedSessionsTests
{
    private int _opens;
    private int _closes;

    private bool NativeOpen(int id)
    {
        _opens++;
        return true;
    }

    private bool NativeClose(int id)
    {
        _closes++;
        return true;
    }

    [Fact]
    public void AListingThatOpensAndClosesAHeldDeviceLeavesItsSessionOpen()
    {
        var sessions = new SharedSessions<int>();

        sessions.Acquire(7, NativeOpen).ShouldBeTrue("the driver connects");
        sessions.Acquire(7, NativeOpen).ShouldBeTrue("a listing opens it to read its serial");
        sessions.Release(7, NativeClose).ShouldBeTrue("and closes it again");

        (_opens, _closes).ShouldBe((1, 0), "the native session opened once and the listing's close did not end it");
        sessions.HoldersOf(7).ShouldBe(1);

        sessions.Release(7, NativeClose).ShouldBeTrue("the driver disconnects");
        (_opens, _closes).ShouldBe((1, 1), "the last holder's close ends the session");
        sessions.HoldersOf(7).ShouldBe(0);
    }

    [Fact]
    public void AFailedFirstOpenHoldsNothing()
    {
        var sessions = new SharedSessions<int>();

        sessions.Acquire(3, static _ => false).ShouldBeFalse();

        sessions.HoldersOf(3).ShouldBe(0, "a device that did not open has no holder, so the next open tries the SDK again");
        sessions.Acquire(3, NativeOpen).ShouldBeTrue();
        _opens.ShouldBe(1);
    }

    [Fact]
    public void ACloseOfADeviceNoHolderOpenedGoesToTheSdk()
    {
        var sessions = new SharedSessions<int>();

        sessions.Release(9, NativeClose).ShouldBeTrue();

        _closes.ShouldBe(1, "a close this count never saw is passed on, as a bare close would have been");
    }

    [Fact]
    public void EachDeviceKeepsItsOwnCount()
    {
        var sessions = new SharedSessions<int>();
        sessions.Acquire(1, NativeOpen);
        sessions.Acquire(2, NativeOpen);

        sessions.Release(2, NativeClose);

        (sessions.HoldersOf(1), sessions.HoldersOf(2), _closes).ShouldBe((1, 0, 1), "closing one camera leaves the other's session alone");
    }
}
