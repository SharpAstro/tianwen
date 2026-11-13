using System;
using System.Threading;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Holds a Bayer capture's mosaics while several colour passes run at once (<see cref="PlanetaryDegrade.MakeBayerAsync"/>): a frame
/// is held from the first pass that reaches it until the last has deposited its photosites, then written out in order. A pass about
/// to deposit a frame <c>maxLead</c> or more past the next one to be written waits (on an event the writer sets after it advances,
/// never by polling), so a capture of any length holds at most <c>maxLead</c> mosaics beyond the slowest pass. The slowest pass's
/// frame is always the next to be written or within one block of it, so the wait cannot deadlock.
/// </summary>
internal sealed class BayerMosaicAssembler(int frames, int pixels, int passes, int maxLead, Action<int, ushort[]> write, Action<int>? written = null)
{
    private readonly ushort[]?[] _mosaic = new ushort[]?[frames];
    private readonly int[] _owed = CreateOwed(frames, passes);
    private ManualResetEventSlim _gate = new(false);
    private int _next;
    private int _draining;

    // A written frame's array is used again for a later frame (a capture of 5,392 frames otherwise allocates as many arrays of
    // the frame's size, each on the large object heap), so the samples handed to write are valid only during that call. A
    // reused array is cleared: a frame takes only the photosites its passes deposit.
    private readonly System.Collections.Concurrent.ConcurrentBag<ushort[]> _spare = [];

    private ushort[] Rent()
    {
        if (_spare.TryTake(out var spare))
        {
            Array.Clear(spare);
            return spare;
        }
        return new ushort[pixels];
    }

    private static int[] CreateOwed(int frames, int passes)
    {
        var owed = new int[frames];
        Array.Fill(owed, passes);
        return owed;
    }

    /// <summary>The next frame to be written; every frame before it has been.</summary>
    public int Next => Volatile.Read(ref _next);

    /// <summary>
    /// Takes one pass's photosites of a frame, after waiting while the frame is <c>maxLead</c> or more past the next to be written.
    /// The wait is re-checked on every wake, and ends with the token's cancellation.
    /// </summary>
    public void Deposit(int index, ReadOnlySpan<ushort> samples, int width, int height, ReadOnlySpan<(int X, int Y)> phases, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Take the gate BEFORE the check: a drain that advances after the check sets the gate held here (it swaps in a
            // fresh one first), so no wake is lost, however many passes wait. One shared event that waiters Reset would lose
            // a wake when one pass resets between another's check and its wait.
            var gate = Volatile.Read(ref _gate);
            if (index - Next < maxLead)
            {
                break;
            }
            gate.Wait(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var fresh = Volatile.Read(ref _mosaic[index]) is null ? Rent() : null;
        var frame = fresh is null
            ? _mosaic[index] ?? throw new InvalidOperationException($"Frame {index} was written out before every colour reached it.")
            : Interlocked.CompareExchange(ref _mosaic[index], fresh, null) ?? fresh;
        if (fresh is not null && !ReferenceEquals(frame, fresh))
        {
            // Another pass placed its array first: this one goes back, unused.
            _spare.Add(fresh);
        }
        foreach (var (px, py) in phases)
        {
            for (var y = py; y < height; y += 2)
            {
                var row = y * width;
                for (var x = px; x < width; x += 2)
                {
                    frame[row + x] = samples[row + x];
                }
            }
        }
        if (Interlocked.Decrement(ref _owed[index]) == 0)
        {
            Drain();
        }
    }

    /// <summary>
    /// Writes the frames every pass has finished, in order, by one thread at a time; a frame finished while another thread drains
    /// is taken by that thread's re-check after it lets go, so no finished frame waits for a later call.
    /// </summary>
    public void Drain()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            {
                return;
            }
            var advanced = false;
            try
            {
                while (_next < frames && Volatile.Read(ref _owed[_next]) == 0)
                {
                    var frame = _mosaic[_next] ?? throw new InvalidOperationException($"Frame {_next} was made by no colour.");
                    write(_next, frame);
                    _mosaic[_next] = null;
                    _spare.Add(frame);
                    Volatile.Write(ref _next, _next + 1);
                    advanced = true;
                    written?.Invoke(_next);
                }
            }
            finally
            {
                // A full fence, so the re-check below cannot read a count older than a depositor's failed attempt to drain.
                Interlocked.Exchange(ref _draining, 0);
                if (advanced)
                {
                    Interlocked.Exchange(ref _gate, new ManualResetEventSlim(false)).Set();
                }
            }
            if (Next >= frames || Volatile.Read(ref _owed[Next]) != 0)
            {
                return;
            }
        }
    }
}
