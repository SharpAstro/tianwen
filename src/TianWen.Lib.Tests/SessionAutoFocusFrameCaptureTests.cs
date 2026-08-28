using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="SessionConfiguration.SaveIntermediates"/>: OFF by default, and when ON it emits
/// the whole V-curve plus the verification exposure as <see cref="FrameType.Focus"/> frames.
/// </summary>
/// <remarks>
/// The frames exist to be a defocus-ladder training corpus, so the two things worth pinning are the
/// ones a consumer depends on and neither the log nor the focus result would reveal: the ladder is
/// COMPLETE (every rung plus the in-focus anchor, in one per-run folder so it reassembles without
/// parsing anything), and every frame is stamped <see cref="FrameType.Focus"/> rather than
/// <see cref="FrameType.Light"/>. The second is the one that matters most: an outer rung sits ~100
/// steps off best focus, so a frame that read as a Light would be swept up by the stacker's scan and
/// quietly soften a master, and nothing about the resulting image would say why.
/// </remarks>
[Collection("Session")]
public class SessionAutoFocusFrameCaptureTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;

    /// <summary>
    /// Session with synthetic stars and the focuser parked off best focus, so the V-curve has a real
    /// minimum to find and the hyperbola converges (which is what produces the verification frame).
    /// </summary>
    private async Task<SessionTestContext> CreateAutoFocusSessionAsync(
        SessionConfiguration config, CancellationToken ct)
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, configuration: config, cancellationToken: ct);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition + 50, ct);
        while (await ctx.Focuser.GetIsMovingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }

        // FakeExternal writes only the FIRST frame to disk by default; a ladder needs all of them.
        ctx.External.MaxFitsWrites = 100;

        return ctx;
    }

    /// <summary>
    /// The fake output root is keyed by the shared helper's caller name, so the Intermediates
    /// subtree carries over between runs and sibling tests. Clear it, or a count is not this run's.
    /// </summary>
    private static string ResetIntermediatesRoot(SessionTestContext ctx)
    {
        var root = Path.Combine(ctx.External.ImageOutputFolder.FullName, "Intermediates");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return root;
    }

    [Fact(Timeout = 60_000)]
    public async Task AutoFocusWritesNothingByDefault()
    {
        var ct = TestContext.Current.CancellationToken;

        // given: the shipped default, stated explicitly rather than assumed
        var config = SessionTestHelper.DefaultConfiguration;
        config.SaveIntermediates.ShouldBeFalse("keeping measurement frames must be opt-in");

        await using var ctx = await CreateAutoFocusSessionAsync(config, ct);
        var intermediatesRoot = ResetIntermediatesRoot(ctx);

        // when
        var (converged, _) = await ctx.Session.AutoFocusAsync(0, ct);

        // then: focus still works, and not one byte was written
        converged.ShouldBeTrue("auto-focus should converge");
        Directory.Exists(intermediatesRoot).ShouldBeFalse("nothing should be written with the flag off");
    }

    [Fact(Timeout = 60_000)]
    public async Task SaveIntermediatesWritesEveryRungAndTheInFocusAnchor()
    {
        var ct = TestContext.Current.CancellationToken;

        var config = SessionTestHelper.DefaultConfiguration with { SaveIntermediates = true };
        await using var ctx = await CreateAutoFocusSessionAsync(config, ct);
        var intermediatesRoot = ResetIntermediatesRoot(ctx);

        // when
        var (converged, _) = await ctx.Session.AutoFocusAsync(0, ct);
        converged.ShouldBeTrue("auto-focus should converge, or there is no verification frame to write");

        // then: one frame per rung, plus the verification exposure at the fitted best focus
        Directory.Exists(intermediatesRoot).ShouldBeTrue();
        var files = Directory.GetFiles(intermediatesRoot, "*.fits", SearchOption.AllDirectories);
        files.Length.ShouldBe(config.AutoFocusStepCount + 1,
            $"expected {config.AutoFocusStepCount} V-curve rungs + 1 verification frame");

        // One run = one directory, which is what makes a ladder reassemblable without parsing names.
        // Its parent is FrameType.ToString(), so this also proves the frame type reached the meta.
        var runDirs = files.Select(f => Path.GetDirectoryName(f)!).Distinct().ToArray();
        runDirs.Length.ShouldBe(1, "one auto-focus run must produce exactly one ladder folder");
        Path.GetFileName(Path.GetDirectoryName(runDirs[0])!).ShouldBe(nameof(FrameType.Focus));

        // Exactly one in-focus anchor, distinguishable without opening the file.
        var names = files.Select(f => Path.GetFileName(f)!).ToArray();
        names.Count(n => n.StartsWith("verify_", StringComparison.Ordinal))
            .ShouldBe(1, "the ladder has exactly one sharp anchor");

        // The rungs must span real focuser travel, or the "ladder" is a stack of identical frames.
        var positions = names
            .Select(n => n.Split("_pos", StringSplitOptions.None)[1].Replace(".fits", "", StringComparison.Ordinal))
            .Select(int.Parse)
            .ToArray();
        (positions.Max() - positions.Min()).ShouldBeGreaterThanOrEqualTo(config.AutoFocusRange / 2,
            "the saved rungs should span most of the configured AF range");

        // And the guarantee the whole design rests on: on disk, in the header the stacker actually
        // reads, these are NOT lights.
        foreach (var file in files)
        {
            Image.TryReadFitsHeader(file, out var info).ShouldBeTrue($"{file} should be a readable FITS");
            info.FrameType.ShouldBe(FrameType.Focus, $"{file} must never read back as a light");
        }
    }
}
