using System;
using System.IO;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public sealed class TestOutputCleanupTests : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Root() => _temp.Create("root").FullName;

    private static string Make(string root, string owner, string sub)
    {
        var dir = TestOutput.Create(root, new DateTime(2026, 11, 6), owner, sub);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
        return dir;
    }

    [Fact]
    public void AFolderIsGoneWhenItsTestEnds()
    {
        var root = Root();
        var dir = Make(root, "test-a", "Sub");
        var other = Make(root, "test-b", "Sub");

        dir.ShouldNotBe(other, "two tests with one sub name must not share a folder");
        TestOutput.Release("test-a", keep: false);

        Directory.Exists(dir).ShouldBeFalse();
        Directory.Exists(other).ShouldBeTrue("another test's live folder stays");
        TestOutput.Release("test-b", keep: false);
        Directory.Exists(Path.Combine(root, "20261106")).ShouldBeFalse("nothing is left under the root");
    }

    [Fact]
    public void ATestAskingTwiceForOneSubGetsOneFolder()
    {
        var root = Root();
        Make(root, "test-c", "Sub").ShouldBe(Make(root, "test-c", "Sub"));
        TestOutput.Release("test-c", keep: false);
    }

    [Fact]
    public void AFolderIsGoneAfterAFailedTestToo()
    {
        var root = Root();
        var dir = Make(root, "test-failed", "Sub");

        // The After hook runs the same release whatever the outcome; a failed test holds no extra state.
        TestOutput.Release("test-failed", keep: false);

        Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public void KeepKeepsTheFolder()
    {
        var root = Root();
        var dir = Make(root, "test-keep", "Sub");

        TestOutput.Release("test-keep", keep: true);

        File.Exists(Path.Combine(dir, "x.txt")).ShouldBeTrue();
        TestOutput.Release("test-keep", keep: false);
    }

    [Fact]
    public void TheRunStartSweepTakesOldDatedFoldersOnly()
    {
        var root = Root();
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        string[] names = ["20261101", "20261104", "20261105", "20261106", "t"];
        foreach (var name in names)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(folder.FullName, "x.txt"), "x");
            folder.LastWriteTimeUtc = now.UtcDateTime.AddDays(-3);
        }

        // A folder with an old name but written to just now belongs to a run still in flight.
        var live = Directory.CreateDirectory(Path.Combine(root, "20261102"));
        live.LastWriteTimeUtc = now.UtcDateTime;

        TestOutput.SweepStale(root, now);

        Directory.Exists(Path.Combine(root, "20261101")).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, "20261104")).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, "20261105")).ShouldBeTrue("yesterday stays");
        Directory.Exists(Path.Combine(root, "20261106")).ShouldBeTrue("today stays");
        Directory.Exists(Path.Combine(root, "t")).ShouldBeTrue("not a dated folder");
        Directory.Exists(live.FullName).ShouldBeTrue("recently written stays");
    }

    [Fact]
    public void ATestThatWroteOutputLeavesNothingBehindOnceItEnds()
    {
        var dir = SharedTestData.CreateTempTestOutputDir();
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
        var id = TestContext.Current.Test?.UniqueID ?? throw new InvalidOperationException("no test");

        // What the assembly's After hook does for this test.
        TestOutput.Release(id, keep: false);

        Directory.Exists(dir).ShouldBeFalse();
    }
}
