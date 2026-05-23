using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TianWen.AI.Imaging;

/// <summary>
/// Default <see cref="IModelResolver"/> -- searches a list of directories in
/// priority order and returns the first match. The default directory list is
/// <c>%LOCALAPPDATA%/TianWen/models</c> first (the path written by
/// <c>tools/tianwen-ai-models-fetch.ps1</c>), then SAS Pro's
/// <c>%LOCALAPPDATA%/SASpro/models</c> if it exists (lets a dual-app dev
/// install share weights). Cross-platform: macOS uses
/// <c>~/Library/Application Support/TianWen/models</c> and Linux uses
/// <c>~/.local/share/TianWen/models</c>, mirroring the fetch script.
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
        throw new FileNotFoundException(
            $"AI model '{modelFileName}' not found in any search path. Run tools/tianwen-ai-models-fetch.ps1 to populate. Probed:{Environment.NewLine}  {probed}");
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
                _logger?.LogDebug("Resolved model '{Name}' to '{Path}'", modelFileName, candidate);
                absolutePath = candidate;
                return true;
            }
        }

        absolutePath = null;
        return false;
    }

    private static ImmutableArray<string> DefaultSearchPaths()
    {
        return [TianWenModelsDir(), SasProModelsDir()];
    }

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
