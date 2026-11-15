using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The tests that change the process-wide <c>GuiTheme</c> (<c>Apply</c>, <c>ToggleNight</c>, <c>CycleTheme</c>), run with
/// NOTHING else in parallel.
/// </summary>
/// <remarks>
/// <para><c>GuiTheme.State</c> and <c>GuiTheme.PaletteGeneration</c> are static, and every render test reads them: they feed
/// sprite cache keys, the Night tint and the palette colours. A <c>[Collection]</c> alone only serialises the classes INSIDE it,
/// so a test that flips the theme still runs beside every render test in another collection. <c>DisableParallelization</c> is
/// what stops that: while this collection runs, the runner starts no other.</para>
/// <para>Why it exists: run 43706162823 (PR #2168, tianwen #2169) failed
/// <c>SkyMapPlanetDiscTests.TheSunsDiscIsItsTexturedPhotosphere</c> (centre (249,169,112), blue under 200) and
/// <c>ADiscWhoseCentreIsOffScreenIsStillDrawnWhereItReaches</c> (322 lit pixels, wanted over 1000) because
/// <c>MeteorShowerCalendarMarkTests</c> (theme Night), in the parallel "UI" collection, called <c>GuiTheme.Apply</c> at the same
/// moment. Each such class leaves the theme as it found it (Dark) in a <c>finally</c>; <c>ThemeCollectionSourceTests</c> fails a
/// class that calls one of the three outside this collection.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThemeCollection
{
    public const string Name = "Theme";
}
