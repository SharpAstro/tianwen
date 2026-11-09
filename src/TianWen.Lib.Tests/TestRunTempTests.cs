using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The run's own temp folder (<see cref="TestRunTemp"/>, #2083): the redirect, a nested host reusing the outer run's folder, the sweep
/// of dead runs, and a delete through read-only files. Every piece is tested on a folder this test owns (<see cref="TempFolders"/>),
/// never on the process's own run folder, with no process started and no wait.
/// </summary>
public sealed class TestRunTempTests : IDisposable
{
    private readonly TempFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Func<string, string?> Env(IReadOnlyDictionary<string, string> values) => name => values.GetValueOrDefault(name);

    private string Folder(string prefix = "run") => _temp.Create(prefix).FullName;

    [Fact]
    public void ThisProcessIsRedirectedToItsOwnRunFolderAndKeepsItsRootsOutsideIt()
    {
        var run = TestRunTemp.Run;
        run.ShouldNotBeNullOrEmpty();
        TestRunTemp.IsRunFolderName(Path.GetFileName(run)).ShouldBeTrue(run);

        foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR" })
        {
            Environment.GetEnvironmentVariable(variable).ShouldBe(run, variable);
        }

        Path.TrimEndingDirectorySeparator(Path.GetTempPath()).ShouldBe(Path.TrimEndingDirectorySeparator(run));
        Environment.GetEnvironmentVariable(TestRunTemp.RunVariable).ShouldBe(run);

        // The run sits directly under the root, and the roots the other helpers keep are that same root, never the run folder.
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(run)).ShouldBe(Path.TrimEndingDirectorySeparator(TestRunTemp.Root));
        TempFolders.Root.ShouldBe(Path.Combine(TestRunTemp.Root, "t"));
        TempFolders.Root.StartsWith(run, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        TestOutput.DefaultRoot.ShouldBe(TestRunTemp.Root);
        TestRunTemp.OriginalTemp.ShouldNotBe(Path.TrimEndingDirectorySeparator(run));
    }

    [Fact]
    public void AFreshRunGetsAShortFolderUnderTheTempItStartedWithAndPointsTheVariablesAtIt()
    {
        var temp = Folder("temp");
        var plan = TestRunTemp.Plan(Env(new Dictionary<string, string>()), temp, "rabcdef12");

        plan.OwnsRun.ShouldBeTrue();
        plan.OriginalTemp.ShouldBe(temp);
        plan.Root.ShouldBe(Path.Combine(temp, "TianWen.Lib" + ".Tests"));
        plan.Run.ShouldBe(Path.Combine(plan.Root, "rabcdef12"));
        Directory.Exists(plan.Run).ShouldBeFalse("a plan creates nothing");

        var variables = TestRunTemp.Variables(plan);
        variables["TMP"].ShouldBe(plan.Run);
        variables["TEMP"].ShouldBe(plan.Run);
        variables["TMPDIR"].ShouldBe(plan.Run);
        variables[TestRunTemp.RunVariable].ShouldBe(plan.Run);
        variables[TestRunTemp.OriginalTempVariable].ShouldBe(temp);
    }

    [Fact]
    public void ARunFolderNameIsShortAndNothingElseUnderTheRootMatchesIt()
    {
        var name = TestRunTemp.NewRunName();
        name.Length.ShouldBe(9);
        TestRunTemp.IsRunFolderName(name).ShouldBeTrue(name);

        TestRunTemp.IsRunFolderName("t").ShouldBeFalse();
        TestRunTemp.IsRunFolderName("20261109").ShouldBeFalse();
        TestRunTemp.IsRunFolderName("rabcdef1").ShouldBeFalse("eight characters in all");
        TestRunTemp.IsRunFolderName("rabcdefgh1").ShouldBeFalse();
        TestRunTemp.IsRunFolderName("rABCDEF12").ShouldBeFalse("hex digits are lower case");
        TestRunTemp.IsRunFolderName("xabcdef12").ShouldBeFalse();
    }

    [Fact]
    public void ANestedHostReusesTheOuterRunsFolderAndOwnsNothing()
    {
        var original = Folder("original");
        var outerRoot = Path.Combine(original, "TianWen.Lib" + ".Tests");
        var outerRun = Directory.CreateDirectory(Path.Combine(outerRoot, "r0a1b2c3d")).FullName;
        var env = new Dictionary<string, string>
        {
            [TestRunTemp.RunVariable] = outerRun,
            [TestRunTemp.OriginalTempVariable] = original,
        };

        // The "current" temp of the nested host is the outer run's folder (it inherited TMP), which is why it must not build a run under it.
        var plan = TestRunTemp.Plan(Env(env), outerRun, "rffffffff");

        plan.OwnsRun.ShouldBeFalse();
        plan.Run.ShouldBe(outerRun);
        plan.Root.ShouldBe(outerRoot);
        plan.OriginalTemp.ShouldBe(original);
        Directory.EnumerateFileSystemEntries(outerRun).ShouldBeEmpty("the nested host made nothing in the outer run");

        var variables = TestRunTemp.Variables(plan);
        variables["TEMP"].ShouldBe(outerRun);
        variables[TestRunTemp.OriginalTempVariable].ShouldBe(original);
    }

    [Fact]
    public void ANestedHostWithNoOriginalVariableFindsTheOriginalTempAboveTheRoot()
    {
        var original = Folder("original");
        var outerRun = Directory.CreateDirectory(Path.Combine(original, "TianWen.Lib" + ".Tests", "r0a1b2c3d")).FullName;

        var plan = TestRunTemp.Plan(Env(new Dictionary<string, string> { [TestRunTemp.RunVariable] = outerRun + Path.DirectorySeparatorChar }), outerRun, "rffffffff");

        plan.OwnsRun.ShouldBeFalse();
        plan.Run.ShouldBe(outerRun);
        plan.OriginalTemp.ShouldBe(original);
    }

    [Fact]
    public void ARunVariableNamingAFolderThatIsGoneMakesAFreshRunUnderTheRealTemp()
    {
        var temp = Folder("temp");
        var env = new Dictionary<string, string>
        {
            [TestRunTemp.RunVariable] = Path.Combine(temp, "gone"),
            [TestRunTemp.OriginalTempVariable] = Path.Combine(temp, "alsogone"),
        };

        var plan = TestRunTemp.Plan(Env(env), temp, "r12345678");

        plan.OwnsRun.ShouldBeTrue();
        plan.OriginalTemp.ShouldBe(temp, "a stale original is not believed either");
        plan.Run.ShouldBe(Path.Combine(temp, "TianWen.Lib" + ".Tests", "r12345678"));
    }

    [Fact]
    public void TheSweepDeletesADeadRunsFolderAndKeepsALiveOne()
    {
        var root = Folder("root");
        var dead = MakeRun(root, "r00000001", out var deadLock);
        deadLock.Dispose();
        File.WriteAllText(Path.Combine(dead, "leftover.txt"), "x");
        var live = MakeRun(root, "r00000002", out var liveLock);

        try
        {
            var deleted = TestRunTemp.SweepDead(root, DateTime.UtcNow);

            deleted.ShouldBe([dead]);
            Directory.Exists(dead).ShouldBeFalse();
            Directory.Exists(live).ShouldBeTrue("its lock is held, so a run is going on in it");
            File.Exists(Path.Combine(live, TestRunTemp.LockName)).ShouldBeTrue();
        }
        finally
        {
            liveLock.Dispose();
        }
    }

    [Fact]
    public void TheSweepNeverTouchesWhatIsNotARunFolderOrTheCallersOwn()
    {
        var root = Folder("root");
        var temps = Directory.CreateDirectory(Path.Combine(root, "t")).FullName;
        var dated = Directory.CreateDirectory(Path.Combine(root, "20261109")).FullName;
        var own = MakeRun(root, "r00000003", out var ownLock);
        ownLock.Dispose();

        TestRunTemp.SweepDead(root, DateTime.UtcNow, except: own).ShouldBeEmpty();

        Directory.Exists(temps).ShouldBeTrue();
        Directory.Exists(dated).ShouldBeTrue();
        Directory.Exists(own).ShouldBeTrue("the caller's own folder is excepted, lock or no lock");
    }

    [Fact]
    public void ARunFolderWithNoLockIsKeptWhileItMayBeStartingAndDeletedAfterTheGrace()
    {
        var root = Folder("root");
        var folder = Directory.CreateDirectory(Path.Combine(root, "r00000004")).FullName;
        var now = DateTime.UtcNow;

        TestRunTemp.SweepDead(root, now).ShouldBeEmpty();
        Directory.Exists(folder).ShouldBeTrue("a run may be between making its folder and taking its lock");

        TestRunTemp.SweepDead(root, now + TestRunTemp.LockGrace + TimeSpan.FromMinutes(1)).ShouldBe([folder]);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void TheSweepOfARootThatDoesNotExistDeletesNothing() =>
        TestRunTemp.SweepDead(Path.Combine(Folder("root"), "absent"), DateTime.UtcNow).ShouldBeEmpty();

    [Fact]
    public void ADeleteGoesThroughReadOnlyFilesAtAnyDepth()
    {
        var folder = Folder("tree");
        var deep = Directory.CreateDirectory(Path.Combine(folder, "objects", "ab")).FullName;
        string[] files = [Path.Combine(folder, "top.txt"), Path.Combine(deep, "object")];
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        TestRunTemp.DeleteTree(folder).ShouldBeTrue();

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void TheRunsEndEmptiesTheFolderAroundTheHeldLockThenDeletesTheFolderOnceTheLockIsReleased()
    {
        var root = Folder("root");
        var run = MakeRun(root, "r00000005", out var runLock);
        var readOnly = Path.Combine(Directory.CreateDirectory(Path.Combine(run, "git", "objects")).FullName, "pack");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        try
        {
            TestRunTemp.DeleteTree(run, keepName: TestRunTemp.LockName).ShouldBeTrue();

            Directory.EnumerateFileSystemEntries(run).Select(Path.GetFileName).ShouldBe([TestRunTemp.LockName]);
        }
        finally
        {
            runLock.Dispose();
        }

        TestRunTemp.DeleteTree(run).ShouldBeTrue();
        Directory.Exists(run).ShouldBeFalse();
    }

    // A run folder as a live run makes it: named as a run, holding its lock file open with no sharing.
    private static string MakeRun(string root, string name, out FileStream runLock)
    {
        var run = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        runLock = new FileStream(Path.Combine(run, TestRunTemp.LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return run;
    }
}
