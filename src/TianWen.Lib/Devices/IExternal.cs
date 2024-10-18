using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace TianWen.Lib.Devices;

public interface IExternal
{
    protected const string ApplicationName = "TianWen";

    public TimeSpan SleepWithOvertime(TimeSpan sleep, TimeSpan extra)
    {
        var adjustedTime = sleep - extra;

        TimeSpan overslept;
        if (adjustedTime >= TimeSpan.Zero)
        {
            overslept = TimeSpan.Zero;
            Sleep(adjustedTime);
        }
        else
        {
            overslept = adjustedTime.Negate();
        }

        return overslept;
    }

    /// <summary>
    /// Uses <see langword="try"/> <see langword="catch"/> to safely execute <paramref name="func"/>.
    /// Returns result or <paramref name="default"/> on failure, and logs result using <see cref="LogException(Exception, string)"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="func"></param>
    /// <param name="default"></param>
    /// <returns>Result or default</returns>
    public T Catch<T>(Func<T> func, T @default = default)
        where T : struct
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, func.Method.Name);
            return @default;
        }
    }

    ILogger AppLogger { get; }

    void Sleep(TimeSpan duration);

    /// <summary>
    /// Folder root where images/flats/logs/... are stored
    /// </summary>
    DirectoryInfo OutputFolder { get; }

    /// <summary>
    /// Folder where profiles are stored
    /// </summary>
    DirectoryInfo ProfileFolder { get; }

    /// <summary>
    /// Time provider that should be used for all time operations
    /// </summary>
    TimeProvider TimeProvider { get; }

    /// <summary>
    /// Creates or returns a sub folder under the <see cref="OutputFolder"/>.
    /// </summary>
    /// <returns></returns>
    public DirectoryInfo CreateSubDirectoryInOutputFolder(params string[] subFolders)
    {
        if (subFolders.Length is 0)
        {
            throw new ArgumentException("At least one subfolder should be specified", nameof(subFolders));
        }

        if (subFolders.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("No subfolder path segment should be empty", nameof(subFolders));
        }

        var subFolderPath = Path.Combine(subFolders.Select(GetSafeFileName).ToArray());

        return Directory.CreateDirectory(Path.Combine(OutputFolder.FullName, subFolderPath));
    }

    public string GetSafeFileName(string name)
    {
        const char ReplacementChar = '_';

        if (name.Trim() == "..")
        {
            return new string(ReplacementChar, 2);
        }

        char[] invalids = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalids.Contains(c) ? ReplacementChar : c).ToArray());
    }
}
