using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests that judge wall-clock latency on the real clock (<see cref="TimingTestGate"/>), run with nothing else in parallel.
/// </summary>
/// <remarks>
/// <c>DisableParallelization</c> is what lets an opted-in run mean what it measures: while the collection runs the runner
/// starts no other, so the thread pool and the cores belong to the test. Same switch, same reason, as the Allocations and
/// Viewer collections. The collection is only the serial slot; the opt-in itself is <see cref="TimingTestGate"/>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    /// <summary>The collection's name, for <c>[Collection(TimingCollection.Name)]</c>.</summary>
    public const string Name = "Timing";
}
