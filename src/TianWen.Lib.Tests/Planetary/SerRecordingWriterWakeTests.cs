using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The tests that hold the whole thread pool, which no other test may share the process with while they run.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeldThreadPoolCollection
{
    public const string Name = "Held thread pool";
}

/// <summary>
/// A recording's writer is woken on its own thread, never through the thread pool (review round 1 of #1621, B2): with every pool
/// thread held, as a machine whose cores are all busy at normal priority holds them, a frame queued after the writer went to wait
/// is still written. Waiting through <c>ReadAllAsync</c>, whose wait keeps no context, it was not, for as long as the pool was held.
/// </summary>
[Collection(HeldThreadPoolCollection.Name)]
public sealed class SerRecordingWriterWakeTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static Image Frame(int seed)
    {
        var plane = new float[4, 8];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                plane[y, x] = seed + y * 8 + x;
            }
        }
        return new Image([plane], BitDepth.Int16, maxValue: seed + 32, minValue: seed, pedestal: 0f, new ImageMeta());
    }

    [Fact(Timeout = 30_000)]
    public void AFrameIsWrittenWhileEveryPoolThreadIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);
        var recording = new SerRecording(Path.Combine(_folders.Create("twwake").FullName, "held.ser"), t0, t0.AddMinutes(1), NullLogger.Instance);
        recording.TryAppend(Frame(100), t0).ShouldBeTrue();
        WaitUntil(() => recording.FramesWritten == 1, ct);

        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxPorts);
        // Never disposed, on purpose (#1628): one held item can still be queued when the release is set (this test's own thread
        // may hold the last place the cap allows), and if it started only after a dispose its Wait would throw
        // ObjectDisposedException out of a pool work item, which ends the test process. Undisposed, a late item finds the event
        // set and returns at once. Nothing leaks: the event makes a kernel handle only if its WaitHandle is asked for, and
        // nothing here asks. Counting the items out instead would need its own wait, and one that ran out would leave the same
        // choice of disposing under a queued item.
        var release = new ManualResetEventSlim(false);
        var held = 0;
        ThreadPool.SetMaxThreads(Environment.ProcessorCount, maxPorts).ShouldBeTrue("premise: the pool can be capped at the core count");
        try
        {
            for (var thread = 0; thread < Environment.ProcessorCount; thread++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    Interlocked.Increment(ref held);
                    release.Wait();
                }, null);
            }
            // Every pool thread there may be is now waiting on the release (this test's own thread may be one of them).
            WaitHeld(() => Volatile.Read(ref held) >= Environment.ProcessorCount - 1, ct).ShouldBeTrue("premise: the pool is held");

            // The writer went to wait after the first frame: this one has to wake it.
            recording.TryAppend(Frame(200), t0.AddSeconds(1)).ShouldBeTrue();
            WaitHeld(() => recording.FramesWritten == 2, ct).ShouldBeTrue("the writer wrote the frame though the pool was held");
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxPorts);
        }

        recording.WriterWaits.ShouldBeGreaterThan(0, "the second frame woke a waiting writer");
        recording.WriterWakesOnPool.ShouldBe(0, "no wait of the writer woke on the pool");
        recording.End("done");
    }

    /// <summary>Held, the pool cannot run xunit's own timeout either, so the wait while it is held carries a bound of its own.</summary>
    private static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(10);

    // The one wall-clock bound in the test, and it has to be one: with the pool held, the test's token cannot be cancelled by its
    // timeout (that runs on the pool), so a writer that never wakes would hang the run rather than fail it.
    private static bool WaitHeld(Func<bool> condition, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > HeldFor || ct.WaitHandle.WaitOne(5))
            {
                return condition();
            }
        }
        return true;
    }

    // A synchronous wait bounded by the test's own token, used before the pool is held.
    private static void WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            if (ct.WaitHandle.WaitOne(5))
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
