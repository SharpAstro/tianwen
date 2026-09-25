using System;
using System.IO;

namespace TianWen.Lib;

public static class SpecialFolderHelper
{
    /// <summary>
    /// The app's folder under <paramref name="folder"/>, always as a full path: the special folder is asked for
    /// WITH the create option, since .NET on Linux answers a folder that does not exist yet (<c>~/Pictures</c> on
    /// a server) with an empty string otherwise, and an empty string combined with <paramref name="appName"/> is a
    /// relative path the working directory resolves. Where there is no such folder to create either (a service
    /// account with no home), <paramref name="fallback"/> is the answer. Nothing is created here but the special
    /// folder itself; the caller creates the result.
    /// </summary>
    /// <param name="getFolderPath"><see cref="Environment.GetFolderPath(Environment.SpecialFolder, Environment.SpecialFolderOption)"/>,
    /// or a stand-in for a test.</param>
    /// <param name="fallback">A full path to use when the special folder cannot be had.</param>
    internal static string ResolveAppSubFolder(Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> getFolderPath,
        Environment.SpecialFolder folder, string appName, string fallback)
    {
        var specialFolder = getFolderPath(folder, Environment.SpecialFolderOption.Create);
        return Path.IsPathFullyQualified(specialFolder) ? Path.Combine(specialFolder, appName) : fallback;
    }

    extension (Environment.SpecialFolder folder)
    {
        public DirectoryInfo CreateAppSubFolder(string appName) =>
            new DirectoryInfo(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create)).CreateSubdirectory(appName);
    }
}
