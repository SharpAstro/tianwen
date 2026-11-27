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
    private static readonly string[] SweepSources = ["WireframeSweepTests.cs", "SweepViewer.cs"];

    [Fact]
    public void SweepSourcesNameNoPlatformFace()
    {
        var root = ChromeMeasuresThroughTheEngineTests.FindSourceRoot();
        root.ShouldNotBeNull("the source tree was not found by walking up from the test binary; the guard would pass over nothing");

        foreach (var name in SweepSources)
        {
            var path = Path.Combine(root, "TianWen.Lib.Tests", name);
            File.Exists(path).ShouldBeTrue(path);

            var code = File.ReadAllLines(path)
                .Select(StripComment)
                .Where(l => l.Contains("ResolveSystemFont"))
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
        => Path.GetFileName(AppFace.Text).ShouldBe(Path.GetFileName(BundledFonts.BundledTextRelativePath),
            "the apps' text face changed; point AppFace and the test project's TestFonts at it");

    private static string StripComment(string line)
    {
        var i = line.IndexOf("//", System.StringComparison.Ordinal);
        return i < 0 ? line : line[..i];
    }
}
