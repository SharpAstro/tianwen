using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly FakeTimeProviderWrapper _timeProvider;

    public FakeExternal(ITestOutputHelper testOutputHelper, FakeTimeProviderWrapper timeProvider, DirectoryInfo? root = null, [CallerMemberName] string? callerName = null)
    {
        // Disable array pooling in tests — parallel tests share the static pool,
        // causing cross-test data races on pooled scratch arrays.
        Array2DPool<float>.Enabled = false;

        _timeProvider = timeProvider;

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

    /// <summary>
    /// Convenience constructor that creates a <see cref="FakeTimeProviderWrapper"/> internally.
    /// Use the 2-arg constructor when you need direct access to the wrapper (e.g. for <c>Advance</c>).
    /// </summary>
    public FakeExternal(ITestOutputHelper testOutputHelper, DirectoryInfo? root = null, DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null, [CallerMemberName] string? callerName = null)
        : this(testOutputHelper, new FakeTimeProviderWrapper(now, autoAdvanceAmount), root, callerName)
    {
    }

    public static ILogger CreateLogger(ITestOutputHelper testOutputHelper) => new XUnitLoggerProvider(testOutputHelper, false).CreateLogger("Test");

    public DirectoryInfo ProfileFolder { get; private set; }

    public DirectoryInfo AppDataFolder { get; private set; }

    public DirectoryInfo ImageOutputFolder { get; private set; }

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

    /// <summary>Maximum number of FITS files to write to disk. Default 1 to reduce test I/O.</summary>
    public int MaxFitsWrites { get; set; } = 1;

    private int _fitsWriteCount;

    /// <summary>
    /// Writes FITS to disk (up to <see cref="MaxFitsWrites"/>), then skips disk I/O for subsequent frames.
    /// Always advances fake time to simulate write duration.
    /// </summary>
    public async ValueTask WriteFitsFileAsync(Image image, string fileName)
    {
        if (Interlocked.Increment(ref _fitsWriteCount) <= MaxFitsWrites)
        {
            var sw = Stopwatch.StartNew();
            await Task.Run(() => image.WriteToFitsFile(fileName)).ConfigureAwait(false);
            sw.Stop();
            _timeProvider.Advance(sw.Elapsed);
        }
        else
        {
            // Skip disk I/O but still advance time (~50ms simulated write)
            _timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        }
    }

    public IReadOnlyList<string> EnumerateAvailableSerialPorts(ResourceLock resourceLock) => [];

    public ValueTask<ResourceLock> WaitForSerialPortEnumerationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ResourceLock.AlwaysUnlocked);

    /// <summary>
    /// Builds a minimal <see cref="IServiceProvider"/> that resolves <see cref="IExternal"/> and <see cref="ITimeProvider"/>.
    /// Use when constructing <see cref="TianWen.Lib.Sequencing.ControllableDeviceBase{TDriver}"/> subclasses in tests.
    /// </summary>
    public IServiceProvider BuildServiceProvider() =>
        new ServiceCollection()
            .AddSingleton<IExternal>(this)
            .AddSingleton<ITimeProvider>(_timeProvider)
            .AddLogging()
            .BuildServiceProvider();
}
