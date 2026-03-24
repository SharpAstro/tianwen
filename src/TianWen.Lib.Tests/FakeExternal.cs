using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
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

    public FakeExternal(ITestOutputHelper testOutputHelper, DirectoryInfo? root = null, DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null, [CallerMemberName] string? callerName = null)
    {
        _timeProvider = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };

        if (string.IsNullOrWhiteSpace(callerName))
        {
            throw new ArgumentNullException(nameof(callerName));
        }

        var testRoot = root ?? new DirectoryInfo(SharedTestData.CreateTempTestOutputDir(callerName));
        ProfileFolder = testRoot.CreateSubdirectory("profiles");
        AppDataFolder = testRoot.CreateSubdirectory("output");
        ImageOutputFolder = testRoot.CreateSubdirectory("images");

        AppLogger = CreateLogger(testOutputHelper);
    }

    public static ILogger CreateLogger(ITestOutputHelper testOutputHelper) => new XUnitLoggerProvider(testOutputHelper, false).CreateLogger("Test");

    public DirectoryInfo ProfileFolder { get; private set; }

    public DirectoryInfo AppDataFolder { get; private set; }

    public DirectoryInfo ImageOutputFolder { get; private set; }

    public TimeProvider TimeProvider => _timeProvider;

    public ILogger AppLogger { get; }

    /// <summary>Optional catalog DB for tests. Set explicitly when catalog-based star rendering is needed.</summary>
    public TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB? CelestialObjectDB { get; set; }

    public ValueTask<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB> GetCelestialObjectDBAsync(CancellationToken cancellationToken = default)
        => CelestialObjectDB is { } db
            ? ValueTask.FromResult(db)
            : throw new InvalidOperationException("CelestialObjectDB not configured in FakeExternal");

    public virtual Task<IUtf8TextBasedConnection> ConnectGuiderAsync(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC, CancellationToken cancellationToken = default)
        => throw new ArgumentException($"No guider connection defined for address={address}", nameof(address));

    public virtual ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding)
        => throw new ArgumentException($"Failed to instantiate serial device at address={address}", nameof(address));

    public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        _timeProvider.Advance(duration);

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
