using System;
using System.IO;

namespace TianWen.Lib;

/// <summary>
/// Shared path constants for the TianWen application family.
/// Used by both the logging infrastructure and <see cref="Devices.External"/>.
/// </summary>
public static class TianWenPaths
{
    internal const string AppName = "TianWen";

    /// <summary>
    /// Root directory for all TianWen data (logs, profiles, output).
    /// Typically <c>%LOCALAPPDATA%\TianWen</c> on Windows, <c>~/.share/TianWen</c> on Linux.
    /// </summary>
    public static DirectoryInfo CommonDataRoot { get; } =
        Environment.SpecialFolder.LocalApplicationData.CreateAppSubFolder(AppName);
}
