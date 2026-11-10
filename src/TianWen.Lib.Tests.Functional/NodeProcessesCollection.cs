using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The tests that spawn a node process, run apart from each other but in parallel with the rest of the suite (#2000).
/// </summary>
/// <remarks>
/// A spawned node starts a runtime, binds a socket and has its own broadcaster, limit watcher and capture loops; several
/// of them starting at once on a shared 2-core CI runner stalled each other for tens of seconds and timed out tests that
/// take a second on a quiet machine. Tests in one collection run one at a time, which is all that is wanted. The
/// collection is deliberately NOT <c>DisableParallelization</c>: that stops every other test in the assembly while one of
/// these runs, and holding the whole suite apart took the job from 13 to 14 minutes to 19.8, past its limit. The Hosting
/// collection was not measured and is left parallel.
/// </remarks>
[CollectionDefinition("NodeProcesses")]
public sealed class NodeProcessesCollection;
