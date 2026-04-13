using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Connections;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface IExternal
{
    /// <summary>
    /// Folder root where app data (logs, planner state, session config) is stored.
    /// Typically <c>%LOCALAPPDATA%/TianWen</c>.
    /// </summary>
    DirectoryInfo AppDataFolder { get; }

    /// <summary>
    /// Default folder where captured FITS images are stored.
    /// Defaults to <c>Pictures/TianWen</c>. Can be overridden per session.
    /// </summary>
    DirectoryInfo ImageOutputFolder { get; }

    /// <summary>
    /// Folder where profiles are stored
    /// </summary>
    DirectoryInfo ProfileFolder { get; }

    /// <summary>
    /// Lazily initialized celestial object database. The DB is initialized on first access
    /// and cached for subsequent calls. Thread-safe.
    /// </summary>
    ValueTask<ICelestialObjectDB> GetCelestialObjectDBAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically writes to a file by writing to a temporary file first, then renaming.
    /// Prevents data loss if the process is interrupted (e.g. Ctrl+C) during write.
    /// </summary>
    public async Task AtomicWriteAsync(string filePath, Func<Stream, CancellationToken, Task> writeAction, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = filePath + ".tmp";
        using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await writeAction(stream, ct);
        }
        File.Move(tmpPath, filePath, overwrite: true);
    }

    /// <summary>
    /// Atomically writes a JSON-serializable value to a file using source-generated serialization.
    /// </summary>
    public Task AtomicWriteJsonAsync<T>(string filePath, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct = default)
        => AtomicWriteAsync(filePath, (stream, token) =>
            System.Text.Json.JsonSerializer.SerializeAsync(stream, value, jsonTypeInfo, token), ct);

    /// <summary>
    /// Reads and deserializes a JSON file using source-generated serialization.
    /// Returns <c>null</c> if the file does not exist or deserialization fails.
    /// </summary>
    public async Task<T?> TryReadJsonAsync<T>(string filePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, ILogger? logger = null, CancellationToken ct = default) where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await System.Text.Json.JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read JSON from {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Creates or returns a sub folder under the <see cref="AppDataFolder"/>.
    /// </summary>
    /// <returns></returns>
    public DirectoryInfo CreateSubDirectoryInAppDataFolder(params string[] subFolders)
    {
        if (subFolders.Length is 0)
        {
            throw new ArgumentException("At least one subfolder should be specified", nameof(subFolders));
        }

        if (subFolders.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("No subfolder path segment should be empty", nameof(subFolders));
        }

        var subFolderPath = Path.Combine([.. subFolders.Select(GetSafeFileName)]);

        return Directory.CreateDirectory(Path.Combine(AppDataFolder.FullName, subFolderPath));
    }

    public string GetSafeFileName(string name)
    {
        const char ReplacementChar = '_';

        if (name.Trim() == "..")
        {
            return new string(ReplacementChar, 2);
        }

        char[] invalids = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalids.Contains(c) ? ReplacementChar : c)]);
    }

    /// <summary>
    /// Delays execution for the specified <paramref name="duration"/>.
    /// <para>
    /// Production (default): delegates to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Test (FakeExternal): auto-advances the <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>
    /// unless <c>ExternalTimePump</c> is set, enabling deterministic time-dependent tests
    /// without an external pump loop.
    /// </para>
    /// </summary>
    ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => new ValueTask(Task.Delay(duration, cancellationToken));

    /// <summary>
    /// TODO: Actually ensure that FITS library writes async
    /// </summary>
    /// <param name="image"></param>
    /// <param name="fileName"></param>
    /// <returns></returns>
    ValueTask WriteFitsFileAsync(Image image, string fileName);

    /// <summary>
    /// Acquires a lock for serial port enumeration.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ResourceLock> WaitForSerialPortEnumerationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns all available serial ports on the system, prefixed with serial: <see cref="ISerialConnection.SerialProto"/>.
    /// Assumes that <see cref="WaitForSerialPortEnumerationAsync(CancellationToken)"/> has been called.
    /// </summary>
    /// <param name="resourceLock">the resource lock obtained from <see cref="WaitForSerialPortEnumerationAsync(CancellationToken)"/></param>
    /// <returns>list of available serial devices, or empty.</returns>
    IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock resourceLock);

    ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding);

    IPEndPoint DefaultGuiderAddress => new IPEndPoint(IPAddress.Loopback, 4400);

    /// <summary>
    /// Connect to an external dedicated guider software at <paramref name="address"/>, using <paramref name="protocol"/> asynchronously.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="protocol"></param>
    /// <returns></returns>
    Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default);

    /// <summary>
    /// Software creator string for FITS SWCREATE header. Includes app name and version.
    /// Override in host applications (CLI, UI) to include the host app name.
    /// </summary>
    string SWCreator
    {
        get
        {
            var asm = typeof(IExternal).Assembly;
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString(3)
                ?? "unknown";
            return $"{SharedStaticData.AppName} {version}";
        }
    }
}
