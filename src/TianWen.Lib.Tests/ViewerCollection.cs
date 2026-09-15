using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The tests that drive a <c>ViewerController</c> and wait on its thread-pool work, run with NOTHING
/// else in parallel.
/// </summary>
/// <remarks>
/// <para>A <c>[Collection]</c> alone only serialises the classes INSIDE it; other collections keep
/// running beside it, and the pool they share is what these tests wait on. <c>DisableParallelization</c>
/// is the switch that actually answers the race: while this collection runs, the runner starts no
/// other, so a <c>Task.Run</c> here is scheduled against an idle pool rather than behind three other
/// collections' work.</para>
/// <para>Why it exists: the run that landed 8.1 failed
/// <c>ViewerControllerTests.SwitchingTheCropOffAndOnAgainRestoresItWithoutScanning</c> on the
/// ubuntu-latest leg with <c>DisplayCrop</c> still null after its 5 s wait, on a 64 x 64 frame whose
/// scan takes microseconds once it runs; the same commit passed on the arm and debug legs, on the PR
/// run before and on both pushes after. The scan had not been SCHEDULED, not failed. The wait's stall
/// bound was widened as well, but a wide bound only makes a starved test slow instead of red; this is
/// what stops it being starved.</para>
/// <para>The cost is the collection's own wall time, a few seconds, spent serially instead of
/// overlapped. The Session collection deliberately does NOT do this (see CLAUDE.md, Test Collections):
/// its tests are long and there are many of them, and its race was in the fakes, not the pool.</para>
/// </remarks>
[CollectionDefinition("Viewer", DisableParallelization = true)]
public sealed class ViewerCollection;
