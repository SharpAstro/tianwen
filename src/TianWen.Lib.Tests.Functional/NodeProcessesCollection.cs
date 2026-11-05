using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The tests that spawn a node process, run with nothing else in parallel: not beside each other, and not beside the
/// in-process node tests (#2000).
/// </summary>
/// <remarks>
/// A spawned node starts a runtime, binds a socket and has its own broadcaster, limit watcher and capture loops; on a
/// shared 2-core CI runner several of them starting at once, beside in-process nodes doing the same, stalled each other
/// for tens of seconds and timed out tests that take a second on a quiet machine. Collections run in parallel with each
/// other; <c>DisableParallelization</c> is what stops the runner starting any other test while one of this collection's
/// runs. The Hosting collection is left parallel unless a measured run says otherwise (see the commit that added this).
/// </remarks>
[CollectionDefinition("NodeProcesses", DisableParallelization = true)]
public sealed class NodeProcessesCollection;
