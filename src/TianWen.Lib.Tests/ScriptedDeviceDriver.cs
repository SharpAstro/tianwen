using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;

namespace TianWen.Lib.Tests;

/// <summary>
/// A driver on <see cref="DeviceDriverBase{TDevice, TDeviceInfo}"/> whose transport and initialisation a test scripts: what
/// opening answers, what initialising answers or throws, and a connect held open until the test lets it go. It counts every
/// open, initialisation and close, and the connection id each close was given.
/// </summary>
internal sealed class ScriptedDeviceDriver(FakeDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<FakeDevice, int>(device, serviceProvider)
{
    internal const int OpenedId = 7;

    private int _opens;
    private int _inits;
    private ImmutableArray<int> _closedIds = [];

    public override string? DriverInfo => "Scripted";

    public override string? Description => "A driver whose connect a test scripts";

    /// <summary>What each open answers, in turn; the last one repeats.</summary>
    public ImmutableArray<bool> OpenAnswers { get; set; } = [true];

    /// <summary>What each initialisation does, in turn: answers true or false, or throws; the last one repeats.</summary>
    public ImmutableArray<Func<bool>> InitAnswers { get; set; } = [static () => true];

    /// <summary>When set, an open waits on it before answering.</summary>
    public TaskCompletionSource? HoldOpen { get; set; }

    public int Opens => Volatile.Read(ref _opens);

    public int Inits => Volatile.Read(ref _inits);

    public ImmutableArray<int> ClosedIds => _closedIds;

    protected override async Task<(bool Success, int ConnectionId, int DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        var open = Interlocked.Increment(ref _opens);
        if (HoldOpen is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return (OpenAnswers[Math.Min(open, OpenAnswers.Length) - 1], OpenedId, 0);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        var init = Interlocked.Increment(ref _inits);
        return ValueTask.FromResult(InitAnswers[Math.Min(init, InitAnswers.Length) - 1]());
    }

    protected override Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        ImmutableInterlocked.Update(ref _closedIds, static (ids, id) => ids.Add(id), connectionId);

        // As SGP's does: only the id it opened closes anything.
        return Task.FromResult(connectionId == OpenedId);
    }
}
