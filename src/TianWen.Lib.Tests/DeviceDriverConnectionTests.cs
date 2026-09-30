using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How a driver on <see cref="DeviceDriverBase{TDevice, TDeviceInfo}"/> connects (#806, the comment on it): a connect that
/// fails leaves nothing open and the driver not connected, so a retry, the resilient call's reconnect among them, runs the
/// whole connect again. It used to read as CONNECTED after an initialisation that refused, skip the initialisation on the
/// retry (a Skywatcher that lost its <c>:e1</c> stayed uninitialised), and hand its disconnect no connection id to close.
/// </summary>
public class DeviceDriverConnectionTests(ITestOutputHelper output)
{
    private ScriptedDeviceDriver Build() => new ScriptedDeviceDriver(new FakeDevice(DeviceType.Mount, 1), new FakeExternal(output).BuildServiceProvider());

    [Fact]
    public async Task AnInitialisationThatRefusesLeavesTheDriverNotConnectedAndItsTransportClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        driver.InitAnswers = [static () => false];

        await Should.ThrowAsync<InvalidOperationException>(driver.ConnectAsync(ct).AsTask());

        driver.Connected.ShouldBeFalse();
        driver.ClosedIds.ShouldBe([ScriptedDeviceDriver.OpenedId], "the transport it opened goes back, by the id it was opened with");
    }

    [Fact]
    public async Task AnInitialisationThatThrowsIsTheReasonTheConnectGives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        driver.InitAnswers = [static () => throw new TimeoutException("no answer to :e1")];

        var thrown = await Should.ThrowAsync<InvalidOperationException>(driver.ConnectAsync(ct).AsTask());

        thrown.InnerException.ShouldBeOfType<TimeoutException>().Message.ShouldBe("no answer to :e1");
        thrown.Message.ShouldContain("no answer to :e1", Case.Sensitive, "a job's Error and the GUI show the message alone");
        driver.Connected.ShouldBeFalse();
        driver.ClosedIds.ShouldBe([ScriptedDeviceDriver.OpenedId]);
    }

    [Fact]
    public async Task AConnectRetriedAfterItsInitialisationRefusedRunsTheInitialisationAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        driver.InitAnswers = [static () => false, static () => true];
        await Should.ThrowAsync<InvalidOperationException>(driver.ConnectAsync(ct).AsTask());

        await driver.ConnectAsync(ct);

        driver.Connected.ShouldBeTrue();
        driver.Opens.ShouldBe(2);
        driver.Inits.ShouldBe(2, "a retry that skipped it left a mount connected and uninitialised");
    }

    [Fact]
    public async Task AConnectRetriedAfterTheTransportRefusedOpensItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        driver.OpenAnswers = [false, true];
        await Should.ThrowAsync<InvalidOperationException>(driver.ConnectAsync(ct).AsTask());

        await driver.ConnectAsync(ct);

        driver.Connected.ShouldBeTrue("the retry used to return as though it had connected, with nothing connected");
        driver.Inits.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoConnectsAtOnceOpenTheTransportOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        driver.HoldOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = driver.ConnectAsync(ct).AsTask();
        var second = driver.ConnectAsync(ct).AsTask();
        driver.HoldOpen.SetResult();
        await first;
        await second;

        driver.Connected.ShouldBeTrue();
        driver.Opens.ShouldBe(1, "a second open is a second handle on an SDK, and a refused one on a COM port");
    }

    [Fact]
    public async Task ADriverWhoseConnectFailedDisposesWithoutClosingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = Build();
        driver.InitAnswers = [static () => false];
        await Should.ThrowAsync<InvalidOperationException>(driver.ConnectAsync(ct).AsTask());

        await Should.NotThrowAsync(driver.DisposeAsync().AsTask());

        driver.ClosedIds.ShouldBe([ScriptedDeviceDriver.OpenedId], "it closed what it opened once, as the connect failed");
    }

    [Fact]
    public async Task ADisconnectClosesTheConnectionItOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var driver = Build();
        await driver.ConnectAsync(ct);

        await driver.DisconnectAsync(ct);

        driver.Connected.ShouldBeFalse();
        driver.ClosedIds.ShouldBe([ScriptedDeviceDriver.OpenedId]);
    }
}
