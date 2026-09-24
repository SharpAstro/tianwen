using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The dataset bake's session staging: the NEXT session's lights are copied to scratch while the
/// current one bakes, a session reads its copies, and every copy is gone once it is no longer needed.
/// Only the read is redirected, so a staged light still names its archive file.
/// </summary>
public sealed class SessionStagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stager-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ImagingSession Session(string name, int lights)
    {
        var dir = Path.Combine(_dir, "archive", name);
        Directory.CreateDirectory(dir);
        var frames = Enumerable.Range(0, lights).Select(i =>
        {
            var path = Path.Combine(dir, $"light_{i:D3}.fits");
            File.WriteAllBytes(path, Enumerable.Range(0, 5000 + i).Select(b => (byte)(b * 7 + i)).ToArray());
            return new FrameInfo(path, 10, 10, 1, BitDepth.Int16, new ImageMeta { Instrument = "cam" });
        });
        return new ImagingSession(dir, name, "cam", name, "", [.. frames]);
    }

    private SessionStager Stager(Func<ImagingSession, bool>? willProcess = null)
        => new(Path.Combine(_dir, "_stage"), willProcess ?? (_ => true)) { StageSameVolume = true };

    [Fact]
    public async Task EachSessionReadsTheCopyMadeWhileThePreviousOneRan_AndTheCopyGoesWhenItIsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        ImagingSession[] sessions = [Session("a", 3), Session("b", 4), Session("c", 2)];
        await using (var stager = Stager())
        {
            // The first session had nothing running before it, so it reads the archive.
            var first = await stager.EnterAsync(sessions, 0, ct);
            first.Lights.ShouldAllBe(l => l.StagedPath == null);

            var second = await stager.EnterAsync(sessions, 1, ct);
            second.Id.ShouldBe(sessions[1].Id);
            second.Lights.Length.ShouldBe(4);
            for (var i = 0; i < 4; i++)
            {
                var light = second.Lights[i];
                light.Path.ShouldBe(sessions[1].Lights[i].Path, "the archive file stays the frame's identity");
                var staged = light.StagedPath.ShouldNotBeNull();
                light.ReadPath.ShouldBe(staged);
                File.ReadAllBytes(staged).ShouldBe(File.ReadAllBytes(light.Path));
            }
            var secondCopy = Path.GetDirectoryName(second.Lights[0].StagedPath).ShouldNotBeNull();

            var third = await stager.EnterAsync(sessions, 2, ct);
            third.Lights.ShouldAllBe(l => l.StagedPath != null);
            Directory.Exists(secondCopy).ShouldBeFalse("the previous session's copy is released on entering the next");
        }

        Directory.Exists(Path.Combine(_dir, "_stage")).ShouldBeFalse("disposing removes every staged file");
    }

    [Fact]
    public async Task ASessionTheBakeWillSkip_IsNotCopied()
    {
        var ct = TestContext.Current.CancellationToken;
        ImagingSession[] sessions = [Session("a", 2), Session("done", 2), Session("c", 2)];
        await using var stager = Stager(willProcess: s => s.Target != "done");
        await stager.EnterAsync(sessions, 0, ct);
        var skipped = await stager.EnterAsync(sessions, 1, ct);
        skipped.Lights.ShouldAllBe(l => l.StagedPath == null);
        var next = await stager.EnterAsync(sessions, 2, ct);
        next.Lights.ShouldAllBe(l => l.StagedPath != null);
    }

    [Fact]
    public async Task ALightWhoseCopyFails_IsReadFromTheArchive()
    {
        var ct = TestContext.Current.CancellationToken;
        ImagingSession[] sessions = [Session("a", 1), Session("b", 3)];
        // One of the next session's lights vanishes before it can be copied.
        File.Delete(sessions[1].Lights[1].Path);
        await using var stager = Stager();
        await stager.EnterAsync(sessions, 0, ct);
        var b = await stager.EnterAsync(sessions, 1, ct);
        b.Lights[0].StagedPath.ShouldNotBeNull();
        b.Lights[1].StagedPath.ShouldBeNull("staging is an acceleration, never a dependency");
        b.Lights[2].StagedPath.ShouldNotBeNull();
    }

    [Fact]
    public async Task LightsOnTheScratchVolume_AreNotCopiedInProduction()
    {
        var ct = TestContext.Current.CancellationToken;
        ImagingSession[] sessions = [Session("a", 1), Session("b", 2)];
        await using var stager = new SessionStager(Path.Combine(_dir, "_stage"), _ => true);
        await stager.EnterAsync(sessions, 0, ct);
        var b = await stager.EnterAsync(sessions, 1, ct);
        b.Lights.ShouldAllBe(l => l.StagedPath == null, "a copy onto the volume the file is already on buys nothing");
    }
}
