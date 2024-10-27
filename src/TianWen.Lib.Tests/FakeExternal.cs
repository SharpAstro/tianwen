using Meziantou.Extensions.Logging.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using TianWen.Lib.Devices;
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

    public virtual ISerialDevice OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => throw new ArgumentException($"Failed to instantiate serial device at {address}", address);

    public void Sleep(TimeSpan duration) => _timeProvider.Advance(duration);
}
