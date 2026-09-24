using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests that measure what a call allocates across EVERY thread, run with nothing else in parallel.
/// </summary>
/// <remarks>
/// <para><c>GC.GetAllocatedBytesForCurrentThread</c> cannot see work a call fans out to the thread pool
/// (star detection runs its passes there), and <c>GC.GetTotalAllocatedBytes</c> sees every test running
/// beside it. <c>DisableParallelization</c> is what makes the process-wide count mean this call: while
/// the collection runs, the runner starts no other. Same switch, same reason, as the Viewer collection.</para>
/// <para>The cost is these tests' own wall time, spent serially. Keep them few and the frames small
/// enough to be quick but large enough that one frame-sized array dwarfs everything else the call
/// allocates, which is the only thing they assert.</para>
/// </remarks>
[CollectionDefinition("Allocations", DisableParallelization = true)]
public sealed class AllocationsCollection;
