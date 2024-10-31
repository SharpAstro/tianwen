using Meziantou.Extensions.Logging.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
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

    public virtual ISerialDevice OpenSerialDevice(DeviceBase device, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => device switch
        {
            FakeDevice fakeDevice when fakeDevice.DeviceType is DeviceType.Mount => new FakeMeadeLX200SerialDevice(true, Encoding.Latin1, _timeProvider, fakeDevice.SiteLatitude, fakeDevice.SiteLongitude),
            _ => throw new ArgumentException($"Failed to instantiate serial device type={device.DeviceType} address={device.Address}", nameof(device))
        };

    public virtual ISerialDevice OpenSerialDevice(string address, int baud, Encoding encoding, TimeSpan? ioTimeout = null)
        => throw new ArgumentException($"Failed to instantiate serial device at address={address}", nameof(address));

    public void Sleep(TimeSpan duration) => _timeProvider.Advance(duration);
}
