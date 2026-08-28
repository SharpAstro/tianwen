using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;

namespace TianWen.AI.Imaging;

/// <summary>
/// Default <see cref="IModelResolver"/> -- searches a list of directories in
/// priority order and returns the first match. The default directory list is
/// the app-local <c>models/</c> beside the running binary first (see
/// <see cref="AppLocalModelsDir"/>), then <c>%LOCALAPPDATA%/TianWen/models</c>
/// (the path written by <c>tools/tianwen-ai-models-fetch.ps1</c>), then SAS Pro's
/// <c>%LOCALAPPDATA%/SASpro/models</c> if it exists (lets a dual-app dev
/// install share weights). Cross-platform: macOS uses
/// <c>~/Library/Application Support/TianWen/models</c> and Linux uses
/// <c>~/.local/share/TianWen/models</c>, mirroring the fetch script.
///
/// <para><b>The app-local directory is not optional garnish -- without it the in-house
/// weights are unreachable outside the test project.</b> They ship in the repo
/// (<c>src/TianWen.AI.Imaging/models/</c>) rather than being fetched per user, and for a
/// while only <c>TianWen.Lib.Tests</c> could load them, because it was the one project that
/// both copied them to its output AND prepended that output to the search list. Every app
/// did neither, so <c>tianwen image sharpen --ai-backend n2n</c> advertised the model,
/// ran a five-minute star removal, and then died on a missing file -- pointing at a fetch
/// script that does not fetch this model. Both halves live in the product now: the copy is a
/// <c>Content</c> item in this project (so it flows to every consumer's output and publish
/// dir) and this is the matching probe.</para>
///
/// <para><b>A vendor that stores a model under a layout of its own is probed where it actually
/// keeps it</b> (see <see cref="GraXpertBuckets"/>) -- a directory entry cannot reach GraXpert's
/// cache, because BOTH the subdirectory (a per-version dir under a per-model bucket) and the file
/// name (<c>model.onnx</c>, the bucket carries the identity) differ from what we ask for. Without
/// it, having GraXpert installed bought nothing: the only bridge was
/// <c>tools/tianwen-ai-models-fetch.ps1</c> hardlinking the file across, which is a repo-relative
/// dev script that a Store install has no way to run -- so "Enhance failed: graxpert_bge.onnx not
/// found" was the shipped outcome of a correctly-installed GraXpert. This is the same courtesy
/// already extended to SAS Pro below, which is probed in its own install directory for the same
/// reason.</para>
/// </summary>
public sealed class ModelResolver : IModelResolver
{
    private readonly ImmutableArray<string> _searchPaths;
    private readonly string _graXpertRoot;
    private readonly ILogger<ModelResolver>? _logger;

    /// <summary>
    /// Use the default search path list (TianWen first, SAS Pro fallback).
    /// </summary>
    public ModelResolver(ILogger<ModelResolver>? logger = null)
        : this(DefaultSearchPaths(), logger)
    {
    }

    /// <summary>
    /// Use a caller-supplied search path list. Probed in order; first match wins.
    /// </summary>
    public ModelResolver(ImmutableArray<string> searchPaths, ILogger<ModelResolver>? logger = null)
        : this(searchPaths, GraXpertRoot(), logger)
    {
    }

    /// <summary>
    /// Test seam: the same resolver against a caller-chosen GraXpert data directory. Not public
    /// because a deployed app must not be able to point the probe somewhere else -- the whole value
    /// of reading the vendor's cache is that it is the location the vendor itself writes.
    /// </summary>
    internal ModelResolver(ImmutableArray<string> searchPaths, string graXpertRoot, ILogger<ModelResolver>? logger)
    {
        if (searchPaths.IsDefault) throw new ArgumentException("searchPaths must be initialised", nameof(searchPaths));
        _searchPaths = searchPaths;
        _graXpertRoot = graXpertRoot;
        _logger = logger;
    }

    public string Resolve(string modelFileName)
    {
        if (TryResolve(modelFileName, out var absolutePath))
        {
            return absolutePath!;
        }

        var probed = string.Join(Environment.NewLine + "  ", CandidateFiles(modelFileName));
        // Name BOTH remedies: the third-party weights are fetched per user, the in-house ones
        // ship in the repo. Naming only the fetch script sends anyone hitting the in-house case to
        // a script that does not have their model. The in-house weights are currently a plain git
        // blob (a .gitattributes exemption from the *.onnx LFS rule), so a checkout has them
        // outright and 'git lfs pull' would be the wrong advice; it is named only as the remedy for
        // the pointer-stub case, which is what a revert of that exemption would reintroduce.
        //
        // A vendor model gets its OWN remedy first, because neither of those two applies to it and
        // both are addressed to someone holding a checkout. The person who hits this is an end user
        // of a packaged install, and "install GraXpert and run it once" is the whole fix.
        var graXpertRemedy = GraXpertRemedy(modelFileName);
        throw new FileNotFoundException(
            $"AI model '{modelFileName}' not found in any search path. {graXpertRemedy}Third-party weights are populated by tools/tianwen-ai-models-fetch.ps1; the in-house models ship in the repo under src/TianWen.AI.Imaging/models/, so a checkout should already have them (if one is a ~130-byte LFS pointer stub instead, run 'git lfs pull'). Probed:{Environment.NewLine}  {probed}");
    }

    public bool TryResolve(string modelFileName, out string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(modelFileName))
            throw new ArgumentException("modelFileName must be non-empty", nameof(modelFileName));
        // Reject both '/' and '\' regardless of OS. The Path.*SeparatorChar
        // lookup collapses to '/' on Linux, letting a Windows-style backslash
        // path slip past on the CI Linux runners (caught by
        // ModelResolverTests.Resolve_RejectsPathSeparators).
        if (modelFileName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"modelFileName must be a bare filename, got '{modelFileName}'", nameof(modelFileName));

        foreach (var candidate in CandidateFiles(modelFileName))
        {
            if (File.Exists(candidate))
            {
                if (IsLfsPointerStub(candidate))
                {
                    // A checkout without git-lfs installed leaves a ~130-byte text pointer where
                    // the weights should be. Handing that to ONNX Runtime fails with an opaque
                    // protobuf parse error, so treat it as absent and keep probing.
                    _logger?.LogWarning(
                        "'{Path}' is a Git LFS pointer stub, not model weights; skipping it. Run 'git lfs pull' (or tools/tianwen-ai-models-fetch.ps1) to materialize it.",
                        candidate);
                    continue;
                }
                _logger?.LogDebug("Resolved model '{Name}' to '{Path}'", modelFileName, candidate);
                absolutePath = candidate;
                return true;
            }
        }

        absolutePath = null;
        return false;
    }

    /// <summary>
    /// The built-in search directories (TianWen's own models dir first, SAS Pro's second),
    /// for callers that want to prepend a path of their own without restating these.
    /// </summary>
    public static ImmutableArray<string> DefaultDirectories => DefaultSearchPaths();

    /// <inheritdoc/>
    public ImmutableArray<string> SearchPaths => _searchPaths;

    /// <inheritdoc/>
    /// <remarks>
    /// Three states, not two, because a Git LFS pointer stub is present-but-not-real: it passes
    /// <c>File.Exists</c>, and handing it to ONNX Runtime fails with an opaque protobuf error.
    /// <see cref="TryResolve"/> already skips one and keeps probing, so a caller asking only
    /// "found?" cannot tell a stub from an absence -- which are different problems with different
    /// remedies ('git lfs pull' vs the fetch script).
    /// </remarks>
    public ModelPresence Probe(string modelFileName)
    {
        var probed = ImmutableArray.CreateBuilder<string>(_searchPaths.Length + 1);
        foreach (var candidate in CandidateFiles(modelFileName))
        {
            probed.Add(candidate);
            if (!File.Exists(candidate)) continue;

            if (IsLfsPointerStub(candidate))
            {
                return new ModelPresence(modelFileName, ModelPresenceKind.PointerStub, candidate, 0, probed.ToImmutable());
            }

            long bytes;
            try
            {
                bytes = new FileInfo(candidate).Length;
            }
            catch (IOException)
            {
                bytes = 0;
            }
            return new ModelPresence(modelFileName, ModelPresenceKind.Present, candidate, bytes, probed.ToImmutable());
        }

        return new ModelPresence(modelFileName, ModelPresenceKind.Absent, null, 0, probed.ToImmutable());
    }

    private static bool IsLfsPointerStub(string path)
    {
        // Pointer files are ~130 bytes of ASCII starting with the spec line below; every real
        // model is orders of magnitude larger, so gate on size before touching the content.
        ReadOnlySpan<byte> lfsSignature = "version https://git-lfs"u8;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024) return false;
            Span<byte> head = stackalloc byte[lfsSignature.Length];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                && head.SequenceEqual(lfsSignature);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every file path that could answer <paramref name="modelFileName"/>, in priority order: the
    /// configured directories probed with the bare name, then any vendor cache that keeps this
    /// model under a layout of its own.
    ///
    /// <para>One enumerator so <see cref="TryResolve"/>, <see cref="Probe"/> and the
    /// <see cref="Resolve"/> failure message can never disagree about what was searched -- a probe
    /// list that omits a location the resolver actually reads is worse than no list, because it is
    /// read as proof the file is not there.</para>
    /// </summary>
    private IEnumerable<string> CandidateFiles(string modelFileName)
    {
        foreach (var dir in _searchPaths)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            yield return Path.Combine(dir, modelFileName);
        }

        foreach (var candidate in GraXpertCandidates(modelFileName))
        {
            yield return candidate;
        }
    }

    /// <summary>
    /// Models another application downloads and owns, keyed by the name TianWen asks for. Each
    /// entry names the vendor's per-model bucket; inside it the vendor keeps one directory per
    /// released model version, each holding a single <c>model.onnx</c> -- the bucket carries the
    /// identity, so the file name is the same for every model the vendor ships and cannot be what
    /// we match on.
    /// </summary>
    private static ImmutableArray<(string ModelFileName, string Bucket)> GraXpertBuckets =>
    [
        // GraXpert's denoise bucket is deliberately absent: it overlaps SAS Pro's AI4 NAFNet,
        // which we already resolve, and nothing here asks for it.
        (OnnxBackgroundExtractor.ModelName, "bge-ai-models"),
    ];

    /// <summary>The vendor's own copy of a model, newest version first, existing paths only.</summary>
    private IEnumerable<string> GraXpertCandidates(string modelFileName)
    {
        foreach (var (name, bucket) in GraXpertBuckets)
        {
            if (!string.Equals(name, modelFileName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var versionDir in VersionDirsNewestFirst(Path.Combine(_graXpertRoot, bucket)))
            {
                yield return Path.Combine(versionDir, "model.onnx");
            }
        }
    }

    /// <summary>
    /// Sub-directories of <paramref name="bucketRoot"/> whose names parse as a version, newest
    /// first. Every version present is offered rather than only the newest, so an install whose
    /// latest download was interrupted still resolves against the copy that completed.
    /// </summary>
    private static IEnumerable<string> VersionDirsNewestFirst(string bucketRoot)
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(bucketRoot)) return [];
            dirs = Directory.GetDirectories(bucketRoot);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return dirs
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Version is not null)
            .OrderByDescending(t => t.Version)
            .Select(t => t.Dir);
    }

    /// <summary>
    /// A remedy sentence for a vendor-owned model, or empty for anything else. Separate from the
    /// generic message because the two remedies it carries -- a repo-relative fetch script and
    /// <c>git lfs pull</c> -- are both addressed to someone holding a checkout, and this model's
    /// user is not.
    /// </summary>
    private string GraXpertRemedy(string modelFileName)
    {
        foreach (var (name, bucket) in GraXpertBuckets)
        {
            if (!string.Equals(name, modelFileName, StringComparison.OrdinalIgnoreCase)) continue;
            return $"This is GraXpert's background-extraction model: install GraXpert (https://github.com/Steffenhir/GraXpert) and run it once so it downloads its AI models, and TianWen picks them up from '{Path.Combine(_graXpertRoot, bucket)}' automatically -- no copying needed. ";
        }
        return string.Empty;
    }

    /// <summary>
    /// GraXpert's per-user data directory (it nests its own name twice). Mirrors the platform
    /// choices of <c>tools/tianwen-ai-models-fetch.ps1</c>, which reads the same tree, and honours
    /// <c>TIANWEN_GRAXPERT_DIR</c> for a non-default install -- the same env-first shape
    /// <c>RcAstroCli.LocateExecutable</c> uses for <c>RC_ASTRO_CLI</c>, and the counterpart of that
    /// script's <c>-GraXpertDir</c>.
    /// </summary>
    private static string GraXpertRoot()
    {
        if (Environment.GetEnvironmentVariable("TIANWEN_GRAXPERT_DIR") is { Length: > 0 } configured)
        {
            return configured;
        }
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GraXpert", "GraXpert");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            return Path.Combine(home, "Library", "Application Support", "GraXpert", "GraXpert");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "GraXpert", "GraXpert");
        }
        var linuxHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return Path.Combine(linuxHome, ".local", "share", "GraXpert", "GraXpert");
    }

    private static ImmutableArray<string> DefaultSearchPaths()
    {
        return [AppLocalModelsDir(), TianWenModelsDir(), SasProModelsDir()];
    }

    /// <summary>
    /// The <c>models/</c> directory beside the running binary -- where this project's
    /// <c>Content</c> item lands the repo's LFS-tracked weights in every consumer's output and
    /// publish directory.
    ///
    /// <para>First in priority because an app-local file is version-matched to the binary that
    /// shipped with it, whereas the per-user cache below is shared across installs and can be
    /// older. That ordering only costs anything if the two disagree, and when they do, the one
    /// built alongside the code is the one to trust.</para>
    /// </summary>
    private static string AppLocalModelsDir() => Path.Combine(AppContext.BaseDirectory, "models");

    private static string TianWenModelsDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "TianWen", "models");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            return Path.Combine(home, "Library", "Application Support", "TianWen", "models");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "TianWen", "models");
        }
        var linuxHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return Path.Combine(linuxHome, ".local", "share", "TianWen", "models");
    }

    private static string SasProModelsDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "SASpro", "models");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            return Path.Combine(home, "Library", "Application Support", "SASpro", "models");
        }
        var linuxHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return Path.Combine(linuxHome, ".local", "share", "SASpro", "models");
    }
}
