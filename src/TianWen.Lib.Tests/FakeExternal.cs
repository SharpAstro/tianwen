using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class FakeExternal : IExternal
{
    private readonly FakeTimeProvider _timeProvider;
    private readonly DirectoryInfo _profileRoot;
    private readonly DirectoryInfo _outputFolder;

    public FakeExternal(ITestOutputHelper testOutputHelper, DirectoryInfo? root = null, DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null)
    {
        _timeProvider = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };

        _profileRoot = root ?? new DirectoryInfo(Path.GetTempPath())
            .CreateSubdirectory(Assembly.GetExecutingAssembly().GetName().Name ?? nameof(FakeExternal))
            .CreateSubdirectory(DateTimeOffset.Now.ToString("yyyyMMdd"))
            .CreateSubdirectory(Guid.NewGuid().ToString("D"));

        _outputFolder = _profileRoot.CreateSubdirectory("output");

        AppLogger = CreateLogger(testOutputHelper);
    }

    public static ILogger CreateLogger(ITestOutputHelper testOutputHelper) => new XUnitLoggerProvider(testOutputHelper, false).CreateLogger("Test");

    public DirectoryInfo ProfileFolder => _profileRoot;

    public DirectoryInfo OutputFolder => _outputFolder;

    public TimeProvider TimeProvider => _timeProvider;

    public ILogger AppLogger { get; }

    public virtual Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default)
        => throw new ArgumentException($"No guider connection defined for address={address}", nameof(address));

    public virtual ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => throw new ArgumentException($"Failed to instantiate serial device at address={address}", nameof(address));

    public void Sleep(TimeSpan duration) => _timeProvider.Advance(duration);

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Sleep(duration);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Advance fake time to match time spent writing <paramref name="image"/> to <paramref name="fileName"/>,
    /// as this is a potentially expensive operation
    /// </summary>
    /// <param name="image"></param>
    /// <param name="fileName"></param>
    public async ValueTask WriteFitsFileAsync(Image image, string fileName)
    {
        // use wall clock time
        var sw = Stopwatch.StartNew();
        await Task.Run(() => image.WriteToFitsFile(fileName)).ConfigureAwait(false);
        sw.Stop();

        _timeProvider.Advance(sw.Elapsed);
    }

    public IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock resourceLock) => [];

    public ValueTask<ResourceLock> WaitForSerialPortEnumerationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ResourceLock.AlwaysUnlocked);
}
