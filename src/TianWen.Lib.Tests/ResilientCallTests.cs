using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

public class ResilientCallTests
{
    private sealed class FakeFlakyDriver : IDeviceDriver
    {
        private readonly Queue<Exception> _failures = new();
        private bool _connected;

        public FakeFlakyDriver(FakeTimeProviderWrapper timeProvider, bool connectedInitially = true, params Exception[] failures)
        {
            TimeProvider = timeProvider;
            _connected = connectedInitially;
            foreach (var f in failures)
            {
                _failures.Enqueue(f);
            }
        }

        public int OpCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        public bool ConnectSucceeds { get; set; } = true;

        public Exception? ConnectException { get; set; }

        public void EnqueueFailure(Exception ex) => _failures.Enqueue(ex);

        public ValueTask<int> OpAsync(CancellationToken ct)
        {
            OpCalls++;
            if (_failures.TryDequeue(out var ex))
            {
                throw ex;
            }
            return ValueTask.FromResult(OpCalls);
        }

        public string Name => "FakeFlaky";
        public string? Description => null;
        public string? DriverInfo => null;
        public string? DriverVersion => "1.0";
        public DeviceType DriverType => DeviceType.Focuser;
        public IExternal External => throw new NotImplementedException();
        public ILogger Logger { get; } = NullLogger.Instance;
        public ITimeProvider TimeProvider { get; }
        public bool Connected => _connected;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            if (ConnectException is { } ex)
            {
                throw ex;
            }
            _connected = ConnectSucceeds;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            return ValueTask.CompletedTask;
        }

#pragma warning disable CS0067 // event is unused — only required to satisfy the interface
        public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
#pragma warning restore CS0067
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private static readonly IOException Transient = new("simulated cable bump");

    [Fact]
    public async Task GivenNoFailuresWhenIdempotentReadThenSucceedsInOneAttempt()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time);

        var result = await ResilientCall.InvokeAsync(
            driver, driver.OpAsync, ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        driver.OpCalls.ShouldBe(1);
        driver.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GivenTwoTransientFailuresWhenIdempotentReadThenRetriesAndSucceeds()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true, Transient, Transient);

        var result = await ResilientCall.InvokeAsync(
            driver, driver.OpAsync, ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken);

        result.ShouldBe(3);
        driver.OpCalls.ShouldBe(3);
        // Driver stayed connected, so no reconnect between attempts.
        driver.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GivenThreeTransientFailuresWhenIdempotentReadThenRethrows()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true, Transient, Transient, Transient);

        var ex = await Should.ThrowAsync<IOException>(async () =>
            await ResilientCall.InvokeAsync(
                driver, driver.OpAsync, ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("simulated cable bump");
        driver.OpCalls.ShouldBe(3);
    }

    [Fact]
    public async Task GivenTransientFailureWhenNonIdempotentActionThenRethrowsImmediately()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true, Transient);

        await Should.ThrowAsync<IOException>(async () =>
            await ResilientCall.InvokeAsync(
                driver, driver.OpAsync, ResilientCallOptions.NonIdempotentAction, TestContext.Current.CancellationToken));

        driver.OpCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GivenNonTransientFailureWhenIdempotentReadThenRethrowsImmediately()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true,
            new InvalidOperationException("caller bug"));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ResilientCall.InvokeAsync(
                driver, driver.OpAsync, ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken));

        driver.OpCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GivenDisconnectedDriverWhenInvokedThenPreReconnects()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: false);

        var result = await ResilientCall.InvokeAsync(
            driver, driver.OpAsync, ResilientCallOptions.NonIdempotentAction, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        driver.ConnectCalls.ShouldBe(1);
        driver.OpCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GivenTransientFailureAfterWhichDisconnectedWhenIdempotentReadThenReconnectsBetweenAttempts()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true, Transient);
        // Simulate: the op() call disconnects the driver on failure.
        // We approximate by flipping Connected to false before the retry.
        var reconnects = 0;

        async ValueTask<int> DisconnectThenOpAsync(CancellationToken ct)
        {
            // After the first throw, mark disconnected so the retry reconnects.
            if (driver.OpCalls == 0)
            {
                await driver.DisconnectAsync(ct);
            }
            return await driver.OpAsync(ct);
        }

        var result = await ResilientCall.InvokeAsync(
            driver, DisconnectThenOpAsync,
            ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken,
            onReconnect: _ => reconnects++);

        result.ShouldBe(2);
        reconnects.ShouldBe(1);
        driver.ConnectCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GivenCancellationWhenIdempotentReadThenThrowsOperationCanceled()
    {
        var time = new FakeTimeProviderWrapper();
        using var cts = new CancellationTokenSource();
        var driver = new FakeFlakyDriver(time);
        cts.Cancel();

        static ValueTask<int> AlwaysThrowCancel(CancellationToken ct) => throw new OperationCanceledException(ct);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ResilientCall.InvokeAsync(
                driver, AlwaysThrowCancel,
                ResilientCallOptions.IdempotentRead, cts.Token));

        // No reconnect, no retry — cancellation is immediate.
        driver.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GivenVoidOverloadWhenNoFailuresThenCallsOpOnce()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time);

        var calls = 0;
        await ResilientCall.InvokeAsync(
            driver, _ =>
            {
                calls++;
                return ValueTask.CompletedTask;
            }, ResilientCallOptions.NonIdempotentAction, TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task GivenOnReconnectCallbackWhenReconnectHappensThenCallbackFires()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: false);
        var callbackCount = 0;

        await ResilientCall.InvokeAsync(
            driver, driver.OpAsync, ResilientCallOptions.NonIdempotentAction, TestContext.Current.CancellationToken,
            onReconnect: _ => callbackCount++);

        callbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task GivenBackoffBetweenAttemptsWhenIdempotentReadThenFakeTimeAdvances()
    {
        var time = new FakeTimeProviderWrapper();
        var driver = new FakeFlakyDriver(time, connectedInitially: true, Transient, Transient);
        var start = time.GetUtcNow();

        await ResilientCall.InvokeAsync(
            driver, driver.OpAsync, ResilientCallOptions.IdempotentRead, TestContext.Current.CancellationToken);

        // Defaults: 250ms before attempt 2, 750ms before attempt 3 = 1000ms total.
        var elapsed = time.GetUtcNow() - start;
        elapsed.ShouldBe(TimeSpan.FromMilliseconds(1000));
    }
}
