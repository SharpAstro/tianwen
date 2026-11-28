using System.IO;
using System.Linq;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The wireframe sweep measures every text in the face the apps draw (<see cref="AppFace.Text"/>), never in the platform's
/// monospace default, so a run on any OS judges what CI judges (#2560). These guards keep it from drifting back.
/// </summary>
[Trait("Category", "Invariant")]
public class SweepMeasuresInTheAppFaceTests
{
    private static readonly string[] SweepSources = ["WireframeSweepTests.cs", "SweepViewer.cs", "LayoutRectSweep.cs"];

    private static readonly string[] PlatformFaceNames = ["ResolveSystemFont", "FontResolver", "Consolas", "DejaVuSansMono", "DejaVu Sans Mono", "Courier"];

    [Fact]
    public void SweepSourcesNameNoPlatformFace()
    {
        var root = ChromeMeasuresThroughTheEngineTests.FindSourceRoot();
        root.ShouldNotBeNull("the source tree was not found by walking up from the test binary; the guard would pass over nothing");

        foreach (var name in SweepSources)
        {
            var path = Path.Combine(root, typeof(SweepMeasuresInTheAppFaceTests).Assembly.GetName().Name!, name);
            File.Exists(path).ShouldBeTrue(path);

            var code = File.ReadAllLines(path)
                .Select(StripComment)
                .Where(l => PlatformFaceNames.Any(n => l.Contains(n, System.StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            code.ShouldBeEmpty($"{name} measures in the platform face; use AppFace.Text (#2560)");
        }
    }

    [Fact]
    public void AppFaceFileIsBesideTheTestBinary()
        => File.Exists(AppFace.Text).ShouldBeTrue(
            $"{AppFace.Text} is missing: every text would measure as nothing and the sweep would pass");

    [Fact]
    public void AppFaceIsTheFaceTheAppsBundle()
    {
        Path.GetFileName(AppFace.Text).ShouldBe(Path.GetFileName(BundledFonts.BundledTextRelativePath),
            "the apps' text face changed; point AppFace and the test project's TestFonts at it");

        // The name alone would pass a stale or different file under the same name: the bytes must be the ones each app ships.
        var root = ChromeMeasuresThroughTheEngineTests.FindSourceRoot();
        root.ShouldNotBeNull("the source tree was not found by walking up from the test binary");
        var face = File.ReadAllBytes(AppFace.Text);
        foreach (var shipped in new[]
        {
            Path.Combine(root, "TianWen.UI.Gui", BundledFonts.BundledTextRelativePath),
            Path.Combine(root, "TianWen.UI.Web", "wwwroot", BundledFonts.BundledTextRelativePath),
        })
        {
            File.Exists(shipped).ShouldBeTrue(shipped);
            File.ReadAllBytes(shipped).ShouldBe(face, $"{shipped} is not the face the sweep measures in");
        }
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf("//", System.StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }
}
