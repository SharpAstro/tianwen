using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Shouldly;
using TianWen.Lib.Logging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Two processes of one app started within the same second used to want the same log file, and the
/// second died at start-up before it had logged a line (a launcher running <c>tianwen --version</c> and
/// then <c>tianwen stack</c> lost the stack, 2026-09-07). The name is kept for the first and gets the
/// process id only on collision.
/// </summary>
public class FileLoggerProviderTests
{
    [Fact]
    public void TwoProvidersInOneSecondGetTwoFilesAndBothLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.FileLoggerProvider", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var first = new FileLoggerProvider("Test", dir);
            using var second = new FileLoggerProvider("Test", dir);

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
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The provider holds its file for writing, so a reader has to permit that.</summary>
    private static string ReadOpenFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
