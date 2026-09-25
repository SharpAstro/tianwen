using System;
using System.IO;

namespace TianWen.Lib;

/// <summary>
/// Shared path constants for the TianWen application family.
/// Used by both the logging infrastructure and <see cref="Devices.External"/>.
/// </summary>
internal static class SharedStaticData
{
    internal const string AppName = "TianWen";

    /// <summary>
    /// Root directory for all TianWen data (logs, profiles, output), public as <see cref="TianWenDataRoot"/>.
    /// Typically <c>%LOCALAPPDATA%\TianWen</c> on Windows, <c>~/.local/share/TianWen</c> on Linux, or the folder
    /// <see cref="TianWenDataRoot.EnvironmentVariable"/> names.
    /// </summary>
    internal static DirectoryInfo CommonDataRoot { get; } = ResolveCommonDataRoot(Environment.GetEnvironmentVariable(TianWenDataRoot.EnvironmentVariable));

    /// <summary>The folder <paramref name="named"/> names, created, or the per-user default when it names none.</summary>
    internal static DirectoryInfo ResolveCommonDataRoot(string? named) => string.IsNullOrWhiteSpace(named)
        ? Environment.SpecialFolder.LocalApplicationData.CreateAppSubFolder(AppName)
        : Directory.CreateDirectory(Path.GetFullPath(named));
}

/// <summary>
/// The per-user folder every TianWen program keeps its data in: profiles, logs, planner pins, the node's socket and
/// lock (<c>%LOCALAPPDATA%\TianWen</c>, <c>$XDG_DATA_HOME/TianWen</c> or <c>~/.local/share/TianWen</c>,
/// <c>~/Library/Application Support/TianWen</c>), unless <see cref="EnvironmentVariable"/> names another. Fixed when
/// the process starts.
/// </summary>
public static class TianWenDataRoot
{
    /// <summary>
    /// Names a folder to use instead: a whole TianWen data tree kept apart from the user's, for a test's node or a
    /// developer's. A node started by a client inherits it, so the two agree on where the data is.
    /// </summary>
    public const string EnvironmentVariable = "TIANWEN_DATA_ROOT";

    public static DirectoryInfo Directory => SharedStaticData.CommonDataRoot;
}
