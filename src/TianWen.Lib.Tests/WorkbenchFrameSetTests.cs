using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Workbench;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stored operation that takes a set of frames by its folder: its result holds how many frame files the folder has.
/// It counts its runs.
/// </summary>
internal sealed class CountFramesOperation : Operation<AddSettings>
{
    public int Runs { get; private set; }

    public override OperationDescription Description { get; } = new(
        "count-frames", Version: 1, Title: "Count", Verb: "none", OperationOutputKind.Stored, ExtraInputs: [], Settings: [])
    {
        TakesFrameSet = true,
    };

    protected override JsonTypeInfo<AddSettings> SettingsInfo => WorkbenchTestJsonContext.Default.AddSettings;

    public override ImmutableArray<string> CommandLine(AddSettings settings, IReadOnlyList<string> inputs, string output)
        => [Description.Id, inputs[0], "-o", output];

    public override ValueTask<OperationOutcome> RunAsync(AddSettings settings, IReadOnlyList<OperationInput> inputs, string outputPath, Action<string>? say,
        CancellationToken cancellationToken)
    {
        Runs++;
        if (inputs[0].Path is not { } folder || !Directory.Exists(folder))
        {
            return ValueTask.FromResult(OperationOutcome.BadInput($"{inputs[0].Name} is no folder of frames"));
        }

        var plane = new float[4, 4];
        plane[0, 0] = FileEnumeration.EnumerateFiles(folder, FrameDigestIndex.FrameFileEndings, recursive: true).Count();
        OperationOutput.Write(new Image([plane], BitDepth.Float32, maxValue: 100f, minValue: 0f, pedestal: 0f, imageMeta: WorkbenchFixtures.Meta()), outputPath, null);
        return ValueTask.FromResult(OperationOutcome.Done);
    }
}

/// <summary>
/// A set of frames as an image's original (#2467): a folder is an image of the project, its key is every frame's place
/// and digest, and the digest index beside the store keeps a frame from being read twice to say the same thing.
/// </summary>
[Collection("Imaging")]
public sealed class WorkbenchFrameSetTests : IDisposable
{
    // Every frame is written under a time the test states, so "its time moved" is a fact here and never the clock's.
    private static readonly DateTime Written = new(2026, 11, 20, 21, 0, 0, DateTimeKind.Utc);

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // A frame is whatever bytes a file of a frame's name holds: the key is made of bytes, and nothing here reads one
    // as an image.
    private static string WriteFrame(string folder, string place, byte fill, int minutesLater = 0)
    {
        var path = Path.Combine(folder, place);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? folder);
        File.WriteAllBytes(path, [.. Enumerable.Repeat(fill, 2880)]);
        File.SetLastWriteTimeUtc(path, Written.AddMinutes(minutesLater));
        return path;
    }

    private static StepReport Step(WorkbenchReport report) => report.Images[0].Steps[0];

    private static string Said(WorkbenchReport report)
        => string.Join("; ", report.Images.Select(static image => image.Refusal).Concat(report.Images.SelectMany(static image => image.Steps).Select(static step => step.Message)));

    [Fact(Timeout = 120_000)]
    public async Task ASetOfFramesIsAnImageWhoseStepRunsOnlyWhenAFrameOfItChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("frame-set-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        var a = WriteFrame(frames, "a.fits", 1);
        var b = WriteFrame(frames, "b.fits", 2);
        WriteFrame(frames, Path.Combine("darks", "c.fit"), 3);
        File.WriteAllText(Path.Combine(frames, "notes.txt"), "no frame");
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("count-frames"))), ct);
        var count = new CountFramesOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([count]));

        var first = await runner.RunAsync(project, cancellationToken: ct);

        first.Succeeded.ShouldBeTrue(Said(first));
        Step(first).State.ShouldBe(StepState.Ran);
        (first.FramesDigested, first.FramesFromIndex).ShouldBe((3, 0));
        WorkbenchFixtures.Read(Step(first).Output.ShouldNotBeNull())[0, 0, 0].ShouldBe(3f);
        var key = first.Images[0].Key;
        key.ShouldNotBeEmpty();
        File.Exists(Path.Combine(project, StepStore.FolderName, FrameDigestIndex.FileName)).ShouldBeTrue("the digests are kept beside the store");

        var second = await runner.RunAsync(project, cancellationToken: ct);

        Step(second).State.ShouldBe(StepState.Kept);
        (second.FramesDigested, second.FramesFromIndex).ShouldBe((0, 3), "no frame is read twice to say the same thing");
        count.Runs.ShouldBe(1);

        // The same bytes under another time: that frame is read again, and nothing runs.
        File.SetLastWriteTimeUtc(a, Written.AddMinutes(5));
        var touched = await runner.StatusAsync(project, cancellationToken: ct);
        Step(touched).State.ShouldBe(StepState.Kept);
        (touched.FramesDigested, touched.FramesFromIndex).ShouldBe((1, 2));
        touched.Images[0].Key.ShouldBe(key);
        // A status keeps what it read, so the next one reads nothing.
        (await runner.StatusAsync(project, cancellationToken: ct)).FramesDigested.ShouldBe(0);

        // A file that is no frame is no part of the set.
        File.WriteAllText(Path.Combine(frames, "notes.txt"), "still no frame");
        (await runner.StatusAsync(project, cancellationToken: ct)).Images[0].Key.ShouldBe(key);

        // Other bytes in a frame: another set, and its step runs.
        WriteFrame(frames, "b.fits", 9, minutesLater: 10);
        var changed = await runner.RunAsync(project, cancellationToken: ct);
        Step(changed).State.ShouldBe(StepState.Ran);
        var changedKey = changed.Images[0].Key;
        changedKey.ShouldNotBe(key);
        changed.Images[0].FileChangedSinceLastRun.ShouldBeTrue();
        count.Runs.ShouldBe(2);

        // A frame renamed is another set too: the scan reads a rejection off a frame's name.
        var rejected = Path.Combine(frames, "BAD_b.fits");
        File.Move(b, rejected);
        var renamed = await runner.StatusAsync(project, cancellationToken: ct);
        Step(renamed).State.ShouldBe(StepState.WillRun);
        renamed.Images[0].Key.ShouldNotBe(changedKey);

        // Put back as it was, the set is the one it was, and its result is still in the store.
        File.Move(rejected, b);
        var restored = await runner.StatusAsync(project, cancellationToken: ct);
        restored.Images[0].Key.ShouldBe(changedKey);
        Step(restored).State.ShouldBe(StepState.Kept);

        // The sidecar that tells the scan a folder's filter is part of the set.
        File.WriteAllText(Path.Combine(frames, ".tianwen-meta.json"), "{}");
        Step(await runner.StatusAsync(project, cancellationToken: ct)).State.ShouldBe(StepState.WillRun);
        count.Runs.ShouldBe(2);
    }

    [Fact(Timeout = 120_000)]
    public async Task AFrameChangedInPlaceUnderItsOldSizeAndTimeIsFoundByVerify()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("frame-verify-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "a.fits", 1);
        WriteFrame(frames, "b.fits", 2);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("count-frames"))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new CountFramesOperation()]));
        Step(await runner.RunAsync(project, cancellationToken: ct)).State.ShouldBe(StepState.Ran);

        // Other bytes, the same size, the same time: the one case the index gives up.
        WriteFrame(frames, "a.fits", 7);
        var stale = await runner.StatusAsync(project, cancellationToken: ct);
        var verified = await runner.StatusAsync(project, new WorkbenchRunOptions { VerifyFrames = true }, ct);
        var after = await runner.StatusAsync(project, cancellationToken: ct);

        Step(stale).State.ShouldBe(StepState.Kept, "a stamp is not a checksum");
        Step(verified).State.ShouldBe(StepState.WillRun);
        (verified.FramesDigested, verified.FramesFromIndex).ShouldBe((2, 0), "a verify reads every frame");
        // The index was corrected, so a plain status says the same from now on, and reads nothing to say it.
        Step(after).State.ShouldBe(StepState.WillRun);
        (after.FramesDigested, after.FramesFromIndex).ShouldBe((0, 2));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFolderIsRefusedWhereItHoldsTheStoreAndByAnOperationThatTakesAnImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var count = new CountFramesOperation();
        var runner = new WorkbenchRunner(new OperationCatalog([count, new AddOperation("add")]));

        // The project's own folder as its frames: a result would be one of the set it was made from.
        var nested = _folders.Create("frames-and-project-").FullName;
        WriteFrame(nested, "a.fits", 1);
        await WorkbenchProjectFile.WriteAsync(nested, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", nested, WorkbenchFixtures.Edit("count-frames"))), ct);

        var refused = await runner.RunAsync(nested, cancellationToken: ct);

        refused.Succeeded.ShouldBeFalse();
        refused.Images[0].Refusal.ShouldNotBeNull().ShouldContain("holds this project's store");
        count.Runs.ShouldBe(0);

        // A folder handed to an operation that reads an image is that step's refusal, in words.
        var project = _folders.Create("frames-as-image-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "a.fits", 1);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("add")),
            WorkbenchFixtures.Picture("counted", frames, WorkbenchFixtures.Edit("count-frames"))), ct);

        var asImage = await runner.RunAsync(project, cancellationToken: ct);

        asImage.Images[0].Steps[0].State.ShouldBe(StepState.Refused);
        asImage.Images[0].Steps[0].Message.ShouldNotBeNull().ShouldContain("is a folder of frames");
        asImage.Images[1].Steps[0].State.ShouldBe(StepState.Ran);

        // And nothing is exported among an original's frames.
        var export = await runner.RunAsync(project, new WorkbenchRunOptions { Image = "counted", ExportPath = Path.Combine(frames, "out.fits") }, ct);

        export.Refusal.ShouldNotBeNull().ShouldContain("among an original's frames");
        File.Exists(Path.Combine(frames, "out.fits")).ShouldBeFalse();
    }

    private static string IndexFile(string project) => Path.Combine(project, StepStore.FolderName, FrameDigestIndex.FileName);

    private async Task<(string Project, string Frames, WorkbenchRunner Runner)> NightAsync(string prefix, params string[] places)
    {
        var project = _folders.Create(prefix).FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        foreach (var place in places)
        {
            WriteFrame(frames, place, 1);
        }

        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("count-frames"))), TestContext.Current.CancellationToken);
        return (project, frames, new WorkbenchRunner(new OperationCatalog([new CountFramesOperation(), new AddOperation("add")])));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFolderWithNoFrameIsRefusedAndTheIndexKeepsWhatItHeldForWhenTheDiskIsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, runner) = await NightAsync("frame-empty-", "a.fits", "b.fits");
        (await runner.StatusAsync(project, cancellationToken: ct)).FramesDigested.ShouldBe(2);
        var held = File.ReadAllText(IndexFile(project));

        // The mount point is there and empty: no set, in words, and nothing forgotten.
        var parked = Path.Combine(Path.GetDirectoryName(frames) ?? frames, "parked");
        Directory.Move(frames, parked);
        Directory.CreateDirectory(frames);
        File.WriteAllText(Path.Combine(frames, "notes.txt"), "no frame");
        var empty = await runner.StatusAsync(project, cancellationToken: ct);

        Said(empty).ShouldContain("holds no frame");
        empty.Images[0].Key.ShouldBeNullOrEmpty();
        File.ReadAllText(IndexFile(project)).ShouldBe(held, "an empty walk is no reason to forget the night");

        Directory.Delete(frames, recursive: true);
        Directory.Move(parked, frames);
        var back = await runner.StatusAsync(project, cancellationToken: ct);

        (back.FramesDigested, back.FramesFromIndex).ShouldBe((0, 2), "the night is not read again");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheIndexDropsAFrameThatIsGoneAndAFolderNoImageNamesAnyMore()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, runner) = await NightAsync("frame-prune-", "a.fits", "b.fits");
        await runner.StatusAsync(project, cancellationToken: ct);
        File.ReadAllText(IndexFile(project)).ShouldContain("b.fits");

        File.Delete(Path.Combine(frames, "b.fits"));
        await runner.StatusAsync(project, cancellationToken: ct);
        var text = File.ReadAllText(IndexFile(project));
        text.ShouldContain("a.fits");
        text.ShouldNotContain("b.fits");

        // The project is pointed at the next night: the old night's entries go.
        var next = Path.Combine(_folders.Create("frames-").FullName, "next");
        WriteFrame(next, "n.fits", 4);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", next, WorkbenchFixtures.Edit("count-frames"))), ct);
        await runner.StatusAsync(project, cancellationToken: ct);
        text = File.ReadAllText(IndexFile(project));
        text.ShouldContain("n.fits");
        text.ShouldNotContain("a.fits");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnIndexThatCannotBeReadKnowsNothingAndIsReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _, runner) = await NightAsync("frame-garbage-", "a.fits", "b.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(IndexFile(project)) ?? project);
        File.WriteAllText(IndexFile(project), "{ this is not json");

        var report = await runner.StatusAsync(project, cancellationToken: ct);

        (report.FramesDigested, report.FramesFromIndex).ShouldBe((2, 0));
        File.ReadAllText(IndexFile(project)).ShouldContain("a.fits");
        (await runner.StatusAsync(project, cancellationToken: ct)).FramesDigested.ShouldBe(0);
    }

    [Fact(Timeout = 120_000)]
    public async Task VerifyReadsAFolderOnceWhateverNumberOfImagesNameItAndReachesRunToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, runner) = await NightAsync("frame-twice-", "a.fits", "b.fits");
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("count-frames")),
            WorkbenchFixtures.Picture("again", frames, WorkbenchFixtures.Edit("count-frames"))), ct);

        var verified = await runner.RunAsync(project, new WorkbenchRunOptions { VerifyFrames = true }, ct);

        verified.Succeeded.ShouldBeTrue(Said(verified));
        (verified.FramesDigested, verified.FramesFromIndex).ShouldBe((2, 0), "two images on one folder read it once");
        verified.Images[0].Key.ShouldBe(verified.Images[1].Key);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStatusRefusesAFolderForAnOperationThatTakesAnImageAsARunDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = _folders.Create("frame-status-").FullName;
        var frames = Path.Combine(_folders.Create("frames-").FullName, "night");
        WriteFrame(frames, "a.fits", 1);
        await WorkbenchProjectFile.WriteAsync(project, WorkbenchFixtures.Project(
            WorkbenchFixtures.Picture("night", frames, WorkbenchFixtures.Edit("add"))), ct);
        var runner = new WorkbenchRunner(new OperationCatalog([new AddOperation("add")]));

        var status = await runner.StatusAsync(project, cancellationToken: ct);

        Step(status).State.ShouldBe(StepState.Refused);
        Step(status).Message.ShouldNotBeNull().ShouldContain("is a folder of frames");
    }

    [Fact(Timeout = 120_000)]
    public async Task ACancelledAskEndsTheWalkBetweenFrames()
    {
        var (project, frames, _) = await NightAsync("frame-cancel-", "a.fits", "b.fits");
        var index = await FrameDigestIndex.OpenAsync(Path.Combine(project, StepStore.FolderName), TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Should.Throw<OperationCanceledException>(() => index.KeyOf(frames, verify: false, out _, cancelled.Token));
        index.Digested.ShouldBe(0);
    }

    [Theory]
    [InlineData("C:\\", "C:\\a.fits", true)]
    [InlineData("C:\\", "C:\\Users\\a.fits", true)]
    [InlineData("C:\\Users", "C:\\Users\\a.fits", true)]
    [InlineData("C:\\Users", "C:\\UsersX\\a.fits", false)]
    [InlineData("/", "/a.fits", true)]
    [InlineData("/mnt/card", "/mnt/card/a.fits", true)]
    [InlineData("/mnt/card", "/mnt/cardx/a.fits", false)]
    public void ARootOfADriveOrOfTheTreeHoldsWhatIsUnderIt(string root, string path, bool expected)
    {
        // The root's own separator is kept by TrimEndingDirectorySeparator, so the prefix must not double it. Each row is
        // judged only on the OS whose separator it uses.
        var separator = root.Contains('\\') ? '\\' : '/';
        if (separator != Path.DirectorySeparatorChar)
        {
            return;
        }

        FrameDigestIndex.IsUnder(path, root).ShouldBe(expected);
    }

    [Fact(Timeout = 120_000)]
    public async Task AFolderHoldingOnlyTheMetadataSidecarIsNoSetAndForgetsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, runner) = await NightAsync("frame-sidecar-", "a.fits", "b.fits");
        (await runner.StatusAsync(project, cancellationToken: ct)).FramesDigested.ShouldBe(2);
        File.ReadAllText(IndexFile(project)).ShouldContain("b.fits");

        File.WriteAllText(Path.Combine(frames, FrameMetaSidecarResolver.FileName), "{}");
        File.Delete(Path.Combine(frames, "a.fits"));
        File.Delete(Path.Combine(frames, "b.fits"));
        var status = await runner.StatusAsync(project, cancellationToken: ct);

        Said(status).ShouldContain("holds no frame");
        var kept = File.ReadAllText(IndexFile(project));
        kept.ShouldContain("a.fits", customMessage: "a sidecar left behind is no reason to forget the night");
        kept.ShouldContain("b.fits", customMessage: "a sidecar left behind is no reason to forget the night");
    }

    [Fact(Timeout = 120_000)]
    public async Task AFrameOfASetThatCannotBeReadRefusesTheSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, runner) = await NightAsync("frame-unreadable-", "a.fits", "b.fits");

        WorkbenchReport status;
        await using (new FileStream(Path.Combine(frames, "b.fits"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Another program holds a frame and shares nothing.
            status = await runner.StatusAsync(project, cancellationToken: ct);
        }

        Said(status).ShouldContain("a frame of it cannot be read");
        status.Images[0].Key.ShouldBeNullOrEmpty();
        status.Succeeded.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task ACancelledStatusEndsAndKeepsTheIndexAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _, runner) = await NightAsync("frame-cancel-keeps-", "a.fits", "b.fits");
        await runner.StatusAsync(project, cancellationToken: ct);
        var held = File.ReadAllText(IndexFile(project));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await runner.StatusAsync(project, cancellationToken: cancelled.Token));

        File.ReadAllText(IndexFile(project)).ShouldBe(held, "what the index learned is kept, and a cancelled walk prunes nothing");
    }

    [Theory]
    [InlineData("C:\\", "C:\\", true)]
    [InlineData("C:\\", "C:\\Frames\\x", true)]
    [InlineData("C:\\Frames", "C:\\Frames", true)]
    [InlineData("C:\\Frames", "C:\\Frames\\x", true)]
    [InlineData("C:\\Frames", "C:\\FramesX\\x", false)]
    [InlineData("/", "/Frames/x", true)]
    [InlineData("/Frames", "/Frames/x", true)]
    [InlineData("/Frames", "/FramesX/x", false)]
    public void AFolderThatIsADriveRootHoldsAnythingUnderIt(string folder, string inner, bool expected)
    {
        // The guards that keep the store and an export out of an original's frames; a root keeps its own separator.
        if ((folder.Contains('\\') ? '\\' : '/') != Path.DirectorySeparatorChar)
        {
            return;
        }

        WorkbenchRunner.HoldsOrIs(folder, inner).ShouldBe(expected);
    }

    [Fact(Timeout = 120_000)]
    public async Task AHandEditedIndexWithNullsKnowsNothingOfThoseEntriesAndIsReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _, runner) = await NightAsync("frame-nulls-", "a.fits", "b.fits");
        Directory.CreateDirectory(Path.GetDirectoryName(IndexFile(project)) ?? project);
        File.WriteAllText(IndexFile(project), "{\"frames\":[null,{\"path\":null,\"digest\":\"x\"},{\"path\":\"/q/a.fits\",\"digest\":null}]}");

        var report = await runner.StatusAsync(project, cancellationToken: ct);

        report.Succeeded.ShouldBeTrue(Said(report));
        (report.FramesDigested, report.FramesFromIndex).ShouldBe((2, 0));
        File.ReadAllText(IndexFile(project)).ShouldContain("b.fits");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnIndexThatCannotBeWrittenIsSaidSoAndNeverFailsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, _, runner) = await NightAsync("frame-unwritable-", "a.fits");
        // A folder where the index file belongs: opening it for a write is refused.
        Directory.CreateDirectory(IndexFile(project));

        var report = await runner.StatusAsync(project, cancellationToken: ct);

        report.FramesDigested.ShouldBe(1);
        report.FramesIndexNotSaved.ShouldBeTrue();
        report.Images[0].Refusal.ShouldBeNullOrEmpty();
    }

    [Fact(Timeout = 120_000)]
    public async Task AWalkCancelledAfterAFolderWasReadStillSavesWhatThatFolderLearnedAndPrunesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, first, _) = await NightAsync("frame-cancel-mid-", "a.fits", "b.fits");
        var second = Path.Combine(_folders.Create("frames-").FullName, "other");
        WriteFrame(second, "z.fits", 5);
        var index = await FrameDigestIndex.OpenAsync(Path.Combine(project, StepStore.FolderName), ct);
        index.KeepOnly([first, second]);
        index.KeyOf(first, verify: false, out _, ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Should.Throw<OperationCanceledException>(() => index.KeyOf(second, verify: false, out _, cancelled.Token));
        await index.SaveAsync(CancellationToken.None);

        var text = File.ReadAllText(IndexFile(project));
        text.ShouldContain("a.fits");
        text.ShouldContain("b.fits");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheVerifyFramesOptionReachesTheRunnerAndCorrectsAFrameChangedInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var (project, frames, _) = await NightAsync("frame-cli-verify-", "a.fits");
        var host = Substitute.For<IConsoleHost>();
        var catalog = new OperationCatalog([new CountFramesOperation(), new AddOperation("add")]);
        var workbench = new WorkbenchSubCommand(host, catalog, new WorkbenchRunner(catalog)).Build();

        (await workbench.Parse(["status", project]).InvokeAsync(cancellationToken: ct)).ShouldBe(0);
        var before = File.ReadAllText(IndexFile(project));

        // Changed in place under the same size and last write time: only a read of the bytes can tell.
        WriteFrame(frames, "a.fits", 9);
        (await workbench.Parse(["status", project]).InvokeAsync(cancellationToken: ct)).ShouldBe(0);
        File.ReadAllText(IndexFile(project)).ShouldBe(before, "without the option the index is trusted");

        (await workbench.Parse(["status", project, "--verify-frames"]).InvokeAsync(cancellationToken: ct)).ShouldBe(0);
        File.ReadAllText(IndexFile(project)).ShouldNotBe(before, "the option reads every frame and corrects the index");
        host.Received(2).WriteScrollable(Arg.Is<string>(line => line.Contains("frames: 1 read for their digest, 0 known by the index")));
        host.Received(1).WriteScrollable(Arg.Is<string>(line => line.Contains("frames: 0 read for their digest, 1 known by the index")));
    }
}
