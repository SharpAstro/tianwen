using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="AiCapabilities"/> and the three-state model probe underneath it.
///
/// <para>The probe's whole reason to exist is that a Git LFS pointer stub is present-but-not-real:
/// it passes <c>File.Exists</c> and is invisible to <see cref="IModelResolver.TryResolve"/>, which
/// skips it and reports absence. Those two states need different remedies, so they are tested
/// separately and with a real stub written to disk rather than a mock -- the detection reads the
/// file's first bytes, so a mock would test nothing.</para>
/// </summary>
public class AiCapabilitiesTests
{
    // Shape of a real pointer file: the detector gates on size, then the leading spec line.
    private static readonly string LfsPointer =
        "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 123456\n";

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tw-aicaps-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Probe_RealFile_ReportsPresentWithPathAndSize()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "weights.onnx");
            File.WriteAllBytes(path, new byte[4096]);

            var presence = new ModelResolver([dir]).Probe("weights.onnx");

            presence.Kind.ShouldBe(ModelPresenceKind.Present);
            presence.Path.ShouldBe(path);
            presence.Bytes.ShouldBe(4096);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Probe_LfsPointerStub_IsItsOwnState_NotAbsent()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "weights.onnx"), LfsPointer);
            var resolver = new ModelResolver([dir]);

            // TryResolve cannot express this: it skips the stub and says "not found", which sends
            // the user to the fetch script when the remedy is 'git lfs pull'.
            resolver.TryResolve("weights.onnx", out _).ShouldBeFalse();

            var presence = resolver.Probe("weights.onnx");
            presence.Kind.ShouldBe(ModelPresenceKind.PointerStub);
            presence.Path.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Probe_Absent_ListsEveryPathItTried()
    {
        var a = NewTempDir();
        var b = NewTempDir();
        try
        {
            var presence = new ModelResolver([a, b]).Probe("nope.onnx");

            presence.Kind.ShouldBe(ModelPresenceKind.Absent);
            presence.Path.ShouldBeNull();
            // The list is the actionable half of a "missing" report: it tells the user where to put
            // the file. Without it the message is unusable on a deployed install.
            presence.ProbedPaths.ShouldBe([Path.Combine(a, "nope.onnx"), Path.Combine(b, "nope.onnx")]);
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public void Probe_EarlierSearchDirWins()
    {
        var first = NewTempDir();
        var second = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(first, "w.onnx"), new byte[16]);
            File.WriteAllBytes(Path.Combine(second, "w.onnx"), new byte[32]);

            // Priority matters in production: the app-local dir is version-matched to the binary,
            // the per-user cache is shared across installs and can be older.
            new ModelResolver([first, second]).Probe("w.onnx").Path
                .ShouldBe(Path.Combine(first, "w.onnx"));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Requirements_NameEveryCapability_WithNoDuplicateFileNames()
    {
        var reqs = AiCapabilities.Requirements;

        reqs.Length.ShouldBeGreaterThan(0);
        reqs.ShouldAllBe(r => r.Capability.Length > 0 && r.FileName.EndsWith(".onnx"));
        // A duplicated file name would mean two capabilities silently share one entry in the report.
        reqs.Select(r => r.FileName).Distinct().Count().ShouldBe(reqs.Length);

        // A multi-variant capability must name each variant, or the report's "(n/m variants)" tally
        // counts rows the reader cannot tell apart.
        foreach (var group in reqs.GroupBy(r => r.Capability).Where(g => g.Count() > 1))
        {
            group.Select(r => r.Variant).Distinct().Count().ShouldBe(group.Count(), group.Key);
            group.ShouldAllBe(r => r.Variant.Length > 0);
        }
    }

    [Fact]
    public async Task ProbeAsync_WithNoRcAstro_LaunchesNothingAndStillReportsModels()
    {
        var dir = NewTempDir();
        try
        {
            // cli: null is the "do not launch anything" contract -- AddRcAstroAi defers its license
            // probe to first use precisely so composing services spawns no process, and a
            // capability probe must not be the thing that breaks that.
            var caps = await AiCapabilities.ProbeAsync(new ModelResolver([dir]), cli: null,
                TestContext.Current.CancellationToken);

            caps.RcAstro.ExecutablePath.ShouldBeNull();
            caps.RcAstro.Products.ShouldBeEmpty();
            caps.Models.Length.ShouldBe(AiCapabilities.Requirements.Length);
            caps.InstallFolder.ShouldNotBeNullOrWhiteSpace();
            caps.Describe().ShouldNotBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Not an assertion about this host -- it prints what the DEFAULT resolver finds here, which is
    /// the whole point of the feature. Asserting on it would pin one developer's model folder.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_DefaultResolver_PrintsThisHostsRealReport()
    {
        var caps = await AiCapabilities.ProbeAsync(new ModelResolver(), cli: null,
            TestContext.Current.CancellationToken);

        foreach (var line in caps.Describe())
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        caps.ModelSearchPaths.ShouldNotBeEmpty();
    }
}
