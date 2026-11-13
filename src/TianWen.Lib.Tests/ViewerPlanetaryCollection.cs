using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer tests that stack, sharpen and derive on a planetary capture: one at a time among themselves, but BESIDE every
/// other collection, unlike <see cref="ViewerCollection"/>, which runs with nothing else.
/// </summary>
/// <remarks>
/// <para>They were in the Viewer collection, and its <c>DisableParallelization</c> made them the suite's serial tail: on the
/// x64 leg of #2054's baseline run (a green PR run of 2026-11-10) the Viewer collection held 199 s of test time plus about
/// 75 s of setup between its tests, all of it after every other collection had finished, with three of the runner's four
/// test threads idle. These five classes were 176 s of the 199.</para>
/// <para>Running alone answers a pool that is slow to schedule the viewer's work (<see cref="ViewerCollection"/> says why).
/// These tests answer it instead by waiting as long as their own <c>Timeout</c> allows
/// (<see cref="ViewerE2E.StartBesideOthers"/>): their work is a derivation or a stack, whose length is the machine's.</para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ViewerPlanetaryCollection
{
    public const string Name = "ViewerPlanetary";
}
