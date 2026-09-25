using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The file logger: one file per process and day. Two processes of one app started within the same second used to
/// want the same log file, and the second died at start-up before it had logged a line (a launcher running
/// <c>tianwen --version</c> and then <c>tianwen stack</c> lost the stack, 2026-09-07); the name gets the process id
/// only on collision. And a process running past local midnight used to go on writing into the day it started,
/// which for a node that runs all week is every night's log in one evening's folder (P1 of
/// docs/plans/hardware-in-the-server.md, #917).
/// </summary>
public class FileLoggerProviderTests
{
    [Fact]
    public void TwoProvidersInOneSecondGetTwoFilesAndBothLog()
    {
        var root = NewLogsRoot();
        try
        {
            using var first = new FileLoggerProvider("Test", root, TimeProvider.System);
            using var second = new FileLoggerProvider("Test", root, TimeProvider.System);

            first.LogFilePath.ShouldNotBe(second.LogFilePath);
            Path.GetFileName(first.LogFilePath).ShouldMatch(@"^Test_\d{8}T\d{2}_\d{2}_\d{2}\.log$");
            Path.GetFileName(second.LogFilePath).ShouldMatch(@"^Test_\d{8}T\d{2}_\d{2}_\d{2}(_\d+)?\.log$");

            second.CreateLogger("t").LogInformation("second is alive");
            first.CreateLogger("t").LogInformation("first is alive");
            ReadOpenFile(second.LogFilePath).ShouldContain("second is alive");
            ReadOpenFile(first.LogFilePath).ShouldContain("first is alive");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ALineLoggedAfterLocalMidnightGoesToTheNewDaysFolder()
    {
        var root = NewLogsRoot();
        try
        {
            // A local zone ahead of UTC, so the local and the UTC date differ around its midnight: the day is LOCAL.
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 13, 59, 58, TimeSpan.Zero));
            clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10"));
            using var provider = new FileLoggerProvider("Node", root, clock);
            var logger = provider.CreateLogger("t");

            logger.LogInformation("the evening's last line");
            var evening = provider.LogFilePath;
            clock.Advance(TimeSpan.FromSeconds(4));
            logger.LogInformation("the first line after midnight");
            var morning = provider.LogFilePath;

            Path.GetFileName(Path.GetDirectoryName(evening)).ShouldBe("20260925");
            Path.GetFileName(Path.GetDirectoryName(morning)).ShouldBe("20260926");
            var eveningText = ReadOpenFile(evening);
            eveningText.ShouldContain("the evening's last line");
            eveningText.ShouldNotContain("the first line after midnight");
            eveningText.ShouldContain($"continued in {morning}");
            var morningText = ReadOpenFile(morning);
            morningText.ShouldContain("the first line after midnight");
            // The new day's file opens as every log does, with where the program came from.
            morningText.Split('\n').First().ShouldContain("TianWen.Build: Node");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string NewLogsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TianWen.FileLoggerProvider", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The provider holds its file for writing, so a reader has to permit that.</summary>
    private static string ReadOpenFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
