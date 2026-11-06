using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Sizes the thread pool for what this test process hosts: several in-process nodes (Kestrel and their hosted services),
/// sessions, event streams and clients of spawned nodes, four tests at a time. On a four-core runner the pool starts with four
/// workers and adds about two a second, so a few blocked workers (a test's deliberate hold, a synchronous wait) queued every
/// await in the process behind them: <see cref="ProcessStallWatch"/> measured a pool work item waiting up to 13.8 s for four
/// and a half minutes, 4 workers busy and 11 items queued, while the CPU, the GC and memory were idle (#2000). Every
/// "flaky" timeout of #2000's repeat runs since (a 2 ms GET taking 17 s, a 503 for a request never answered, a recording of
/// no frames) was that queue. A minimum of 64 workers lets blocked ones be replaced at once; the watch still reports any
/// starvation left.
/// </summary>
internal static class TestThreadPool
{
    internal const int MinimumWorkers = 64;

    // A test assembly is the one place a module initializer belongs: nothing else runs before the first test.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Size()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MinimumWorkers), Math.Max(io, MinimumWorkers));
    }
#pragma warning restore CA2255
}
