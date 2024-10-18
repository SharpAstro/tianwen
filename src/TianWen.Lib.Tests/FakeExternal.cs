using Meziantou.Extensions.Logging.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System;
using System.IO;
using System.Reflection;
using TianWen.Lib.Devices;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

internal class FakeExternal : IExternal
{
    private readonly FakeTimeProvider _timeProvider;
    private readonly DirectoryInfo _profileRoot;
    private readonly DirectoryInfo _outputFolder;

    public FakeExternal(ITestOutputHelper outputHelper, DirectoryInfo? root = null, DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null)
    {
        _timeProvider = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };

        _profileRoot = root ?? new DirectoryInfo(Path.GetTempPath())
            .CreateSubdirectory(Assembly.GetExecutingAssembly().GetName().Name ?? nameof(FakeExternal))
            .CreateSubdirectory(Guid.NewGuid().ToString("D"));

        _outputFolder = _profileRoot.CreateSubdirectory("output");

        AppLogger = new XUnitLoggerProvider(outputHelper, appendScope: false).CreateLogger("Test");
    }

    public DirectoryInfo ProfileFolder => _profileRoot;

    public DirectoryInfo OutputFolder => _outputFolder;

    public TimeProvider TimeProvider => _timeProvider;

    public ILogger AppLogger { get; }

    public void Sleep(TimeSpan duration) => _timeProvider.Advance(duration);
}
