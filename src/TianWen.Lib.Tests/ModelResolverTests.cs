using System;
using System.Collections.Immutable;
using System.IO;
using Shouldly;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ModelResolverTests : IDisposable
{
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
}
