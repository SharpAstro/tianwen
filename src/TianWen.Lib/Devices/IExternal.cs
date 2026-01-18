using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface IExternal
{
    protected const string ApplicationName = "TianWen";

    public async ValueTask<TimeSpan> SleepWithOvertimeAsync(TimeSpan sleep, TimeSpan extra, CancellationToken cancellationToken = default)
    {
        var adjustedTime = sleep - extra;

        TimeSpan overslept;
        if (adjustedTime >= TimeSpan.Zero)
        {
            overslept = TimeSpan.Zero;
            await SleepAsync(adjustedTime, cancellationToken);
        }
        else
        {
            overslept = adjustedTime.Negate();
        }

        return overslept;
    }

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

    void Sleep(TimeSpan duration);

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

    ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null);

    IPEndPoint DefaultGuiderAddress => new IPEndPoint(IPAddress.Loopback, 4400);

    /// <summary>
    /// Connect to an external dedicated guider software at <paramref name="address"/>, using <paramref name="protocol"/> asynchronously.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="protocol"></param>
    /// <returns></returns>
    Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default);
}
