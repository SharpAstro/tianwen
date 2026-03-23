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
    /// Uses <see langword="try"/> <see langword="catch"/> to safely execute <paramref name="action"/>.
    /// Returns <see langword="true"/> on success and  <see langword="false"/> failure, and logs errors using <see cref="AppLogger"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="action"></param>
    /// <returns>true if success</returns>
    public bool Catch(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, action.Method.Name);
            return false;
        }
    }    
    
    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning default <paramref name="default"/> if an exception occured.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="asyncFunc"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="default"></param>
    /// <returns></returns>
    public async ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
    {
        try
        {
            await asyncFunc(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning default <paramref name="default"/> if an exception occured.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="asyncFunc"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="default"></param>
    /// <returns></returns>
    public async ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
    {
        try
        {
            return await asyncFunc(cancellationToken);
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return @default;
        }
    }

    /// <summary>
    /// If <paramref name="condition"/> is true, awaits <paramref name="asyncFunc"/>, returning <paramref name="default"/> on exception or when condition is false.
    /// </summary>
    public ValueTask<T> CatchAsyncIf<T>(bool condition, Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => condition ? CatchAsync(asyncFunc, cancellationToken, @default) : ValueTask.FromResult(@default);

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning default <paramref name="default"/> if an exception occured.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="asyncFunc"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="default"></param>
    /// <returns></returns>
    public async Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
    {
        try
        {
            await asyncFunc(cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously awaits <paramref name="asyncFunc"/>, returning default <paramref name="default"/> if an exception occured.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="asyncFunc"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="default"></param>
    /// <returns></returns>
    public async Task<T> CatchAsync<T>(Func<CancellationToken, Task<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
    {
        try
        {
            return await asyncFunc(cancellationToken);
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex, "Exception {Message} while executing: {Method}", ex.Message, asyncFunc.Method.Name);
            return @default;
        }
    }

    /// <summary>
    /// Uses <see langword="try"/> <see langword="catch"/> to safely execute <paramref name="func"/>.
    /// Returns result or <paramref name="default"/> on failure, and logs errors using <see cref="AppLogger"/>.
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

    ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default);

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
    public async Task<T?> TryReadJsonAsync<T>(string filePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct = default) where T : class
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
            AppLogger.LogWarning(ex, "Failed to read JSON from {FilePath}", filePath);
            return null;
        }
    }

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

        var subFolderPath = Path.Combine([.. subFolders.Select(GetSafeFileName)]);

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
        return new string([.. name.Select(c => invalids.Contains(c) ? ReplacementChar : c)]);
    }

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
