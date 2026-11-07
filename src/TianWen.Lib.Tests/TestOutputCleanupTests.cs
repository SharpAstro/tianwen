using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public sealed class TestOutputCleanupTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 11, 6);

    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Root() => _temp.Create("root").FullName;

    private static string Make(TestOutputFolders folders, string root, string owner, string sub)
    {
        var dir = folders.Create(root, Day, owner, sub);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
        return dir;
    }

    [Fact]
    public void AFolderIsGoneWhenItsTestEnds()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var dir = Make(folders, root, "test-a", "Sub");
        var other = Make(folders, root, "test-b", "Sub");

        dir.ShouldNotBe(other, "two tests with one sub name must not share a folder");
        folders.Release("test-a", keep: false);

        Directory.Exists(dir).ShouldBeFalse();
        Directory.Exists(other).ShouldBeTrue("another test's live folder stays");
        folders.Release("test-b", keep: false);
        Directory.Exists(other).ShouldBeFalse();
    }

    [Fact]
    public void TheDateFolderIsTheRunsToRemoveNeverATests()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var dir = Make(folders, root, "test-a", "Sub");
        var dateFolder = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("no parent");

        // A sibling test may be between making this folder and its own below it, so the last test out never removes it.
        folders.Release("test-a", keep: false);
        Directory.Exists(dateFolder).ShouldBeTrue("a test's end leaves the (now empty) date folder to the run's end");

        folders.ReleaseAll(keep: false);
        Directory.Exists(dateFolder).ShouldBeFalse("the run's end removes it, so a green run leaves nothing under the root");
    }

    [Fact]
    public void TheRunsEndLeavesADateFolderThatIsNotEmpty()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var mine = Make(folders, root, "test-a", "Sub");
        var dateFolder = Path.GetDirectoryName(mine) ?? throw new InvalidOperationException("no parent");
        var strangers = Directory.CreateDirectory(Path.Combine(dateFolder, "another-process")).FullName;

        folders.ReleaseAll(keep: false);

        Directory.Exists(mine).ShouldBeFalse();
        Directory.Exists(strangers).ShouldBeTrue("a folder this registry never made is not its to delete");
    }

    // SessionTestHelper names a session's folder "<test method>/<random>", so the folder made is <date>/<method>/<random>_<random>:
    // the registry owned the leaf and the date folder, and nothing owned <method>, which stayed behind empty (and kept the date
    // folder from ever being empty): 70 folders after a green unit run on Linux, 110 after a functional one.
    [Theory]
    [InlineData("Method/abcd1234")]
    [InlineData("Method\\abcd1234")]
    [InlineData("Group/Method/abcd1234")]
    public void ASubNameWithSeparatorsLeavesNoParentBehindOnceTheRunEnds(string sub)
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var dir = Make(folders, root, "test-a", sub);
        var dateFolder = Path.Combine(root, Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        folders.Release("test-a", keep: false);
        folders.ReleaseAll(keep: false);

        Directory.Exists(dir).ShouldBeFalse();
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("nothing the registry made may outlive the run, a parent of a nested sub name included");
        Directory.Exists(dateFolder).ShouldBeFalse();
    }

    [Fact]
    public void ACasesParentIsAnotherCasesLiveFolderUntilTheRunEnds()
    {
        // The cases of one theory run in parallel under one method name, so they share <date>/<method>: the first one out
        // must not take it from under the second, which may be between making it and making its own folder below it.
        var root = Root();
        var folders = new TestOutputFolders();
        var first = Make(folders, root, "case-1", "Method/aaaa1111");
        var second = Make(folders, root, "case-2", "Method/bbbb2222");
        var parent = Path.GetDirectoryName(first) ?? throw new InvalidOperationException("no parent");
        Path.GetDirectoryName(second).ShouldBe(parent, "both cases sit under their method's folder");

        folders.Release("case-1", keep: false);
        Directory.Exists(parent).ShouldBeTrue("another case's folder is still in it");
        Directory.Exists(second).ShouldBeTrue();

        folders.Release("case-2", keep: false);
        Directory.Exists(parent).ShouldBeTrue("a test's end leaves the (now empty) parent to the run's end, as for the date folder");

        folders.ReleaseAll(keep: false);
        Directory.Exists(parent).ShouldBeFalse();
    }

    [Fact]
    public void TheRunsEndLeavesAParentThatIsNotEmpty()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var mine = Make(folders, root, "test-a", "Method/abcd1234");
        var parent = Path.GetDirectoryName(mine) ?? throw new InvalidOperationException("no parent");
        var dateFolder = Path.GetDirectoryName(parent) ?? throw new InvalidOperationException("no date folder");
        var strangers = Directory.CreateDirectory(Path.Combine(parent, "another-process")).FullName;

        folders.ReleaseAll(keep: false);

        Directory.Exists(mine).ShouldBeFalse();
        Directory.Exists(strangers).ShouldBeTrue("a folder this registry never made is not its to delete");
        Directory.Exists(parent).ShouldBeTrue("and neither is the parent that holds it");
        Directory.Exists(dateFolder).ShouldBeTrue("nor the date folder above that");
    }

    [Fact]
    public void ATestAskingTwiceForOneSubGetsOneFolder()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        Make(folders, root, "test-c", "Sub").ShouldBe(Make(folders, root, "test-c", "Sub"));
        folders.Release("test-c", keep: false);
    }

    [Fact]
    public void KeepKeepsTheFolder()
    {
        var root = Root();
        var folders = new TestOutputFolders();
        var dir = Make(folders, root, "test-keep", "Sub");

        folders.Release("test-keep", keep: true);
        folders.ReleaseAll(keep: true);

        File.Exists(Path.Combine(dir, "x.txt")).ShouldBeTrue();
        folders.ReleaseAll(keep: false);
    }

    [Fact]
    public void ADeleteThatFailedWhenItsTestEndedIsTriedAgainWhenTheRunEnds()
    {
        var root = Root();
        List<string> attempts = [];
        var holdOnce = true;
        var folders = new TestOutputFolders(folder =>
        {
            attempts.Add(folder);
            if (holdOnce)
            {
                holdOnce = false;
                throw new IOException("held by a process the test started");
            }

            TestOutputFolders.DeleteTree(folder);
        });
        var dir = Make(folders, root, "test-held", "Sub");

        folders.Release("test-held", keep: false);
        Directory.Exists(dir).ShouldBeTrue("the first delete failed, and a failed test's end never throws");

        folders.ReleaseAll(keep: false);

        Directory.Exists(dir).ShouldBeFalse("the run's end tries again, when the process holding it has let go");
        attempts.Count.ShouldBe(2);
        attempts.ShouldAllBe(a => a == dir);
        Directory.Exists(Path.GetDirectoryName(dir)).ShouldBeFalse("and the date folder goes once it is empty");
    }

    [Fact]
    public void ADeleteThatNeverSucceedsFailsNothing()
    {
        var root = Root();
        var attempts = 0;
        var folders = new TestOutputFolders(_ =>
        {
            attempts++;
            throw new UnauthorizedAccessException("a read-only file in it");
        });
        var dir = Make(folders, root, "test-stuck", "Sub");

        folders.Release("test-stuck", keep: false);
        folders.ReleaseAll(keep: false);
        folders.ReleaseAll(keep: false);

        Directory.Exists(dir).ShouldBeTrue("left for the next run's sweep");
        Directory.Exists(Path.GetDirectoryName(dir)).ShouldBeTrue("its date folder is not empty");
        attempts.ShouldBe(3, "once at the test's end, then once per run end; no wait, no loop");
    }

    [Fact]
    public void AFolderTheRunItselfHoldsIsDeletedOnceAtTheRunsEnd()
    {
        // A run's own folder (a class or assembly fixture's) fails its delete in the same pass that remembers it, so retrying
        // it straight away is the same delete again with no wait between: one attempt, then it is the next run's sweep's.
        var root = Root();
        var attempts = 0;
        var folders = new TestOutputFolders(_ =>
        {
            attempts++;
            throw new IOException("held by a process the fixture started");
        });
        var dir = Make(folders, root, TestOutput.RunOwner, "Shared");

        folders.ReleaseAll(keep: false);

        attempts.ShouldBe(1);
        Directory.Exists(dir).ShouldBeTrue("left for the next run's sweep");
    }

    [Fact]
    public void TheRunStartSweepTakesOldDatedFoldersByTheirNameOnly()
    {
        var root = Root();
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        string[] names = ["20261101", "20261102", "20261104", "20261105", "20261106", "t"];
        foreach (var name in names)
        {
            // Every folder was written to just now, so none is old by its last-write time: only its name can say.
            var folder = Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(folder.FullName, "x.txt"), "x");
        }

        TestOutput.SweepStale(root, now);

        Directory.Exists(Path.Combine(root, "20261101")).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, "20261102")).ShouldBeFalse("a recent last-write time does not keep a folder of an old date");
        Directory.Exists(Path.Combine(root, "20261104")).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, "20261105")).ShouldBeTrue("yesterday stays");
        Directory.Exists(Path.Combine(root, "20261106")).ShouldBeTrue("today stays");
        Directory.Exists(Path.Combine(root, "t")).ShouldBeTrue("not a dated folder");
    }
}
