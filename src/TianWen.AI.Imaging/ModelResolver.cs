using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

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
/// </summary>
public sealed class ModelResolver : IModelResolver
{
    private readonly ImmutableArray<string> _searchPaths;
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
    {
        if (searchPaths.IsDefault) throw new ArgumentException("searchPaths must be initialised", nameof(searchPaths));
        _searchPaths = searchPaths;
        _logger = logger;
    }

    public string Resolve(string modelFileName)
    {
        if (TryResolve(modelFileName, out var absolutePath))
        {
            return absolutePath!;
        }

        var probed = string.Join(Environment.NewLine + "  ", _searchPaths.Select(p => Path.Combine(p, modelFileName)));
        // Name BOTH remedies: the third-party weights are fetched per user, the in-house ones
        // ship in the repo. Naming only the fetch script sends anyone hitting the in-house case to
        // a script that does not have their model. The in-house weights are currently a plain git
        // blob (a .gitattributes exemption from the *.onnx LFS rule), so a checkout has them
        // outright and 'git lfs pull' would be the wrong advice; it is named only as the remedy for
        // the pointer-stub case, which is what a revert of that exemption would reintroduce.
        throw new FileNotFoundException(
            $"AI model '{modelFileName}' not found in any search path. Third-party weights are populated by tools/tianwen-ai-models-fetch.ps1; the in-house models ship in the repo under src/TianWen.AI.Imaging/models/, so a checkout should already have them (if one is a ~130-byte LFS pointer stub instead, run 'git lfs pull'). Probed:{Environment.NewLine}  {probed}");
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

        foreach (var dir in _searchPaths)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var candidate = Path.Combine(dir, modelFileName);
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
        var probed = ImmutableArray.CreateBuilder<string>(_searchPaths.Length);
        foreach (var dir in _searchPaths)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, modelFileName);
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
