using Meziantou.Extensions.Logging.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit.Abstractions;

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
            .CreateSubdirectory(Guid.NewGuid().ToString("D"));

        _outputFolder = _profileRoot.CreateSubdirectory("output");
        AppLogger = new XUnitLoggerProvider(testOutputHelper, false).CreateLogger("Test");
    }

    public DirectoryInfo ProfileFolder => _profileRoot;

    public DirectoryInfo OutputFolder => _outputFolder;

    public TimeProvider TimeProvider => _timeProvider;

    public ILogger AppLogger { get; }

    public virtual IUtf8TextBasedConnection ConnectGuider(EndPoint address, CommunicationProtocol protocol = CommunicationProtocol.JsonRPC)
        => throw new ArgumentException($"No guider connection defined for address {address}", nameof(address));

    public IReadOnlyList<string> EnumerateSerialPorts() => [];

    public virtual ISerialConnection OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => throw new ArgumentException($"Failed to instantiate serial device at address={address}", nameof(address));

    public void Sleep(TimeSpan duration) => _timeProvider.Advance(duration);

    /// <summary>
    /// Advance fake time to match time spent writing <paramref name="image"/> to <paramref name="fileName"/>,
    /// as this is a potentially expensive operation
    /// </summary>
    /// <param name="image"></param>
    /// <param name="fileName"></param>
    public void WriteFitsFile(Image image, string fileName)
    {
        // use wall clock time
        var sw = Stopwatch.StartNew();
        image.WriteToFitsFile(fileName);
        sw.Stop();

        _timeProvider.Advance(sw.Elapsed);
    }
}
