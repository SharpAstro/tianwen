using System;
using System.Collections.Immutable;
using System.IO;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ModelResolverTests : IDisposable
{
    /// <summary>
    /// Every per-user default directory comes from ONE root, so a fourth cannot quietly grow its
    /// own idea of where application data lives.
    /// </summary>
    /// <remarks>
    /// This pins the shape, not the drift it replaced -- and the difference matters. Until now
    /// three hand-rolled platform switches computed this root independently and two had already
    /// diverged: <c>SasProModelsDir</c> ignored <c>XDG_DATA_HOME</c> while its siblings honoured
    /// it, so a Linux user who set it had SAS Pro's models looked for in the wrong place. That
    /// specific bug is NOT what fails here, because reproducing it needs <c>XDG_DATA_HOME</c> set
    /// in the environment, and an env var is process-global while these tests run in parallel --
    /// the fixture would be flakier than the thing it guards. What this catches is the next copy:
    /// any default directory that stops deriving from
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> fails on every platform.
    /// </remarks>
    [Fact]
    public void EveryPerUserDefaultDirectoryDerivesFromTheSameRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(localAppData),
            "platform returned no LocalApplicationData (see dotnet/runtime#109614)");

        var defaults = ModelResolver.DefaultDirectories;
        defaults.Length.ShouldBeGreaterThan(1);

        // The first entry is app-local (beside the binary), deliberately not a per-user path.
        defaults[0].ShouldStartWith(AppContext.BaseDirectory);
        foreach (var dir in defaults.RemoveAt(0))
        {
            dir.StartsWith(localAppData, StringComparison.Ordinal).ShouldBeTrue(
                $"'{dir}' does not derive from LocalApplicationData, so it is a second source of truth");
        }
    }

    private readonly string _temp;
    private readonly string _primary;
    private readonly string _fallback;

    public ModelResolverTests()
    {
        // Two scratch dirs to act as the primary + fallback search paths.
        _temp = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests.ModelResolver." + Guid.NewGuid().ToString("N")[..8]);
        _primary = Path.Combine(_temp, "primary");
        _fallback = Path.Combine(_temp, "fallback");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_fallback);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }

    private ModelResolver MakeResolver() => new([_primary, _fallback]);

    [Fact]
    public void TryResolve_FindsFileInPrimary()
    {
        var path = Path.Combine(_primary, "foo.onnx");
        File.WriteAllText(path, "placeholder");

        MakeResolver().TryResolve("foo.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(path);
    }

    [Fact]
    public void TryResolve_FallsBackToSecondary()
    {
        var path = Path.Combine(_fallback, "foo.onnx");
        File.WriteAllText(path, "placeholder");

        MakeResolver().TryResolve("foo.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(path);
    }

    [Fact]
    public void TryResolve_PrefersPrimaryOverFallback()
    {
        var pPath = Path.Combine(_primary, "foo.onnx");
        var fPath = Path.Combine(_fallback, "foo.onnx");
        File.WriteAllText(pPath, "p");
        File.WriteAllText(fPath, "f");

        MakeResolver().TryResolve("foo.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(pPath);
    }

    [Fact]
    public void TryResolve_ReturnsFalseWhenMissing()
    {
        MakeResolver().TryResolve("notthere.onnx", out var resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ThrowsWithProbedPaths()
    {
        var ex = Should.Throw<FileNotFoundException>(() => MakeResolver().Resolve("notthere.onnx"));
        ex.Message.ShouldContain(_primary);
        ex.Message.ShouldContain(_fallback);
        ex.Message.ShouldContain("notthere.onnx");
        ex.Message.ShouldContain("tianwen-ai-models-fetch.ps1");
    }

    /// <summary>
    /// The in-house weights ship beside the binary, so the DEFAULT list must probe there -- and
    /// first, because that copy is version-matched to the code. Skipping it is not a degraded
    /// search, it is a model that cannot be loaded at all outside a project that hand-builds its
    /// own list, which is exactly the state <c>--ai-backend n2n</c> shipped in.
    /// </summary>
    [Fact]
    public void DefaultDirectories_LeadWithTheModelsDirBesideTheBinary()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "models");

        ModelResolver.DefaultDirectories[0].ShouldBe(expected);
    }

    /// <summary>
    /// The per-user cache and the SAS Pro share stay reachable behind it -- the app-local entry is
    /// an addition, not a replacement, so a fetch-script install keeps working.
    /// </summary>
    [Fact]
    public void DefaultDirectories_StillFallBackToThePerUserCaches()
    {
        var dirs = ModelResolver.DefaultDirectories;

        dirs.Length.ShouldBe(3);
        dirs[1].ShouldEndWith(Path.Combine("TianWen", "models"));
        dirs[2].ShouldEndWith(Path.Combine("SASpro", "models"));
    }

    /// <summary>
    /// Both remedies must be named: the fetch script for third-party weights, git-lfs for the
    /// in-house ones. Naming only the script is what sent a real investigation to a tool that
    /// does not have the missing model.
    /// </summary>
    [Fact]
    public void Resolve_NamesBothWaysToPopulateAModel()
    {
        var ex = Should.Throw<FileNotFoundException>(() => MakeResolver().Resolve("notthere.onnx"));

        ex.Message.ShouldContain("tianwen-ai-models-fetch.ps1");
        ex.Message.ShouldContain("git lfs pull");
    }

    [Fact]
    public void Resolve_RejectsPathSeparators()
    {
        var r = MakeResolver();
        Should.Throw<ArgumentException>(() => r.Resolve("subdir/foo.onnx"));
        Should.Throw<ArgumentException>(() => r.Resolve("subdir\\foo.onnx"));
    }

    [Fact]
    public void Resolve_RejectsEmptyName()
    {
        var r = MakeResolver();
        Should.Throw<ArgumentException>(() => r.Resolve(""));
        Should.Throw<ArgumentException>(() => r.Resolve(" "));
    }

    [Fact]
    public void Constructor_RejectsDefaultArray()
    {
        Should.Throw<ArgumentException>(() => new ModelResolver(default(ImmutableArray<string>)));
    }

    // ---- GraXpert's own model cache -------------------------------------------------------
    //
    // GraXpert writes 'bge-ai-models/<semver>/model.onnx'. Neither half of that is reachable from a
    // directory entry probed with a bare name: the version dir is not known ahead of time and the
    // file is called model.onnx, because for GraXpert the BUCKET carries the identity. So a plain
    // search path can never find it, which is why an installed GraXpert used to buy nothing.

    private string MakeGraXpertBge(string version, string content = "weights")
    {
        var dir = Path.Combine(_temp, "graxpert", "bge-ai-models", version);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "model.onnx");
        File.WriteAllText(path, content);
        return path;
    }

    private ModelResolver MakeResolverWithGraXpert() =>
        new([_primary, _fallback], Path.Combine(_temp, "graxpert"), null);

    /// <summary>
    /// The regression this whole seam exists for: GraXpert installed, nothing copied anywhere, and
    /// the model resolves. Before it, the ONLY bridge was tools/tianwen-ai-models-fetch.ps1
    /// hardlinking the file into TianWen's models tree -- a repo-relative dev script, so the Store
    /// build of Astro Photo Viewer failed its Enhance action on a machine where GraXpert was
    /// correctly installed and its weights were sitting on disk.
    /// </summary>
    [Fact]
    public void TryResolve_ReadsGraXpertsOwnModelCache()
    {
        var expected = MakeGraXpertBge("1.0.1");

        MakeResolverWithGraXpert().TryResolve("graxpert_bge.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(expected);
    }

    /// <summary>
    /// Version dirs sort by VERSION, not by name -- '1.0.10' is newer than '1.0.9', which ordinal
    /// string ordering gets backwards.
    /// </summary>
    [Fact]
    public void TryResolve_PrefersTheNewestGraXpertVersion()
    {
        MakeGraXpertBge("1.0.9");
        var newest = MakeGraXpertBge("1.0.10");

        MakeResolverWithGraXpert().TryResolve("graxpert_bge.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(newest);
    }

    /// <summary>
    /// A copy in TianWen's own tree still wins -- the fetch script's hardlink keeps working, and it
    /// outlives GraXpert being uninstalled, so it stays the higher-priority source.
    /// </summary>
    [Fact]
    public void TryResolve_PrefersTheConfiguredPathsOverGraXpert()
    {
        var ours = Path.Combine(_primary, "graxpert_bge.onnx");
        File.WriteAllText(ours, "p");
        MakeGraXpertBge("1.0.1");

        MakeResolverWithGraXpert().TryResolve("graxpert_bge.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(ours);
    }

    /// <summary>
    /// The vendor probe is keyed on the model NAME. Every other model must be unaffected -- the
    /// bucket holds exactly one file called model.onnx, so a name-blind probe would hand GraXpert's
    /// background-extraction weights to whatever asked next.
    /// </summary>
    [Fact]
    public void TryResolve_DoesNotOfferGraXpertsFileToAnotherModel()
    {
        MakeGraXpertBge("1.0.1");

        MakeResolverWithGraXpert().TryResolve("darkstar_color_AI4.onnx", out var resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// An install whose newest download was interrupted still resolves: every version present is
    /// offered, so a truncated 1.0.2 falls through to the complete 1.0.1 rather than failing.
    /// </summary>
    [Fact]
    public void TryResolve_FallsThroughAnUnusableNewerGraXpertVersion()
    {
        var good = MakeGraXpertBge("1.0.1");
        // A pointer-stub-shaped file stands in for "present but not weights", the one case
        // TryResolve is required to skip and keep probing.
        MakeGraXpertBge("1.0.2", "version https://git-lfs.github.com/spec/v1");

        MakeResolverWithGraXpert().TryResolve("graxpert_bge.onnx", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(good);
    }

    /// <summary>
    /// With GraXpert absent the message must say so in terms its reader can act on. The two
    /// standing remedies -- a repo-relative fetch script and 'git lfs pull' -- are both addressed to
    /// someone holding a checkout, and the person who hits this is running a Store install.
    /// </summary>
    [Fact]
    public void Resolve_NamesGraXpertAsTheRemedyForItsOwnModel()
    {
        var ex = Should.Throw<FileNotFoundException>(
            () => MakeResolverWithGraXpert().Resolve("graxpert_bge.onnx"));

        ex.Message.ShouldContain("GraXpert");
        ex.Message.ShouldContain(Path.Combine(_temp, "graxpert", "bge-ai-models"));
    }

    /// <summary>
    /// ...and it stays out of the way for every other model, which has nothing to do with GraXpert.
    /// </summary>
    [Fact]
    public void Resolve_DoesNotMentionGraXpertForAnUnrelatedModel()
    {
        var ex = Should.Throw<FileNotFoundException>(
            () => MakeResolverWithGraXpert().Resolve("notthere.onnx"));

        ex.Message.ShouldNotContain("GraXpert");
    }

    /// <summary>
    /// GraXpert's cache is probed alongside the directory list, so it must appear in the diagnostic
    /// too. A probe list that omits a location the resolver really reads is worse than none: it is
    /// read as proof the file is not there.
    /// </summary>
    [Fact]
    public void Probe_ReportsTheGraXpertCandidate()
    {
        var expected = MakeGraXpertBge("1.0.1");

        var presence = MakeResolverWithGraXpert().Probe("graxpert_bge.onnx");

        presence.Kind.ShouldBe(ModelPresenceKind.Present);
        presence.Path.ShouldBe(expected);
        presence.ProbedPaths.ShouldContain(expected);
    }

    /// <summary>
    /// The invariant has to hold in the case that MATTERS. With GraXpert installed the probe list
    /// named its cache; with GraXpert absent it named nothing at all -- and absent is exactly when
    /// a user reads the list, so it held only when nobody needed it. A placeholder cannot resolve,
    /// which is the point: it answers "did you look, and where would it go?" without pretending a
    /// file is there.
    /// </summary>
    [Fact]
    public void Probe_NamesTheGraXpertLocationEvenWhenGraXpertIsNotInstalled()
    {
        // Deliberately no MakeGraXpertBge(...) -- nothing is installed.
        var presence = MakeResolverWithGraXpert().Probe("graxpert_bge.onnx");

        presence.Kind.ShouldBe(ModelPresenceKind.Absent);
        presence.ProbedPaths.ShouldContain(p => p.Contains("bge-ai-models", StringComparison.Ordinal));
    }

    /// <summary>The placeholder must never be mistaken for a real answer.</summary>
    [Fact]
    public void TryResolve_DoesNotResolveThePlaceholderPath()
    {
        MakeResolverWithGraXpert().TryResolve("graxpert_bge.onnx", out var resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// The list must name every location, INCLUDING the ones after the one that answered. Probe
    /// used to stop at the first hit, so a resolved model reported only the prefix of the search
    /// that happened to contain it -- and on a machine where anything earlier answers (a
    /// fetch-script hardlink in TianWen's own tree, say) GraXpert's cache never appeared at all.
    /// Its absence reads as "not supported" rather than "not reached", which is the opposite of
    /// what a reader should conclude.
    /// </summary>
    [Fact]
    public void Probe_ListsTheGraXpertLocationEvenWhenSomethingEarlierAnswered()
    {
        var earlier = Path.Combine(_primary, "graxpert_bge.onnx");
        File.WriteAllText(earlier, "weights");
        var vendorCopy = MakeGraXpertBge("1.0.1");

        var presence = MakeResolverWithGraXpert().Probe("graxpert_bge.onnx");

        // The earlier copy still wins the verdict...
        presence.Kind.ShouldBe(ModelPresenceKind.Present);
        presence.Path.ShouldBe(earlier);
        // ...but the search did not stop being described there.
        presence.ProbedPaths.ShouldContain(vendorCopy);
    }

    /// <summary>Same guarantee for a model that lives in none of the vendor buckets.</summary>
    [Fact]
    public void Probe_ListsEveryConfiguredDirectoryEvenWhenTheFirstAnswered()
    {
        File.WriteAllText(Path.Combine(_primary, "foo.onnx"), "p");

        var presence = MakeResolverWithGraXpert().Probe("foo.onnx");

        presence.Kind.ShouldBe(ModelPresenceKind.Present);
        presence.ProbedPaths.ShouldContain(Path.Combine(_fallback, "foo.onnx"));
    }
}
