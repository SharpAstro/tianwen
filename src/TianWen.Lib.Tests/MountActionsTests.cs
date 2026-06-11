using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="MountActions.SlewToJ2000Async"/>. Uses NSubstitute
/// to stub <see cref="IMountDriver"/> (including default-interface methods like
/// <c>TryTransformJ2000ToMountNativeAsync</c> and <c>EnsureTrackingAsync</c>) so
/// we don't need a full transform + site stack. Same style as
/// <see cref="LiveSessionActionsTests"/>.
/// </summary>
public class MountActionsTests
{
    private const double M31_RA_J2000 = 0.712;
    private const double M31_DEC_J2000 = 41.27;
    // Helper's Sun/Moon branch only keys on CatalogIndex.Sol / .Moon. For non-solar-system
    // targets any index is equivalent; use null to avoid coupling to a specific Messier constant.
    private static readonly CatalogIndex? NonSolarSystem = null;

    [Fact]
    public async Task SlewToJ2000Async_WhenMountNotConnected_ReturnsSlewNotPossibleWithoutTouchingDriver()
    {
        var mount = Substitute.For<IMountDriver>();
        mount.Connected.Returns(false);

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.SlewNotPossible);
        msg.ShouldBe("Mount is not connected");
        await mount.DidNotReceiveWithAnyArgs().EnsureTrackingAsync(Arg.Any<TrackingSpeed>(), Arg.Any<CancellationToken>());
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenCannotSlewAsync_ReturnsSlewNotPossible()
    {
        var mount = Substitute.For<IMountDriver>();
        mount.Connected.Returns(true);
        mount.CanSlewAsync.Returns(false);

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.SlewNotPossible);
        msg.ShouldContain("does not support async slewing");
        await mount.DidNotReceiveWithAnyArgs().EnsureTrackingAsync(Arg.Any<TrackingSpeed>(), Arg.Any<CancellationToken>());
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_HappyPath_EnsuresSiderealTrackingAndCommitsSlew()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar });

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to M31");
        await mount.Received(1).EnsureTrackingAsync(TrackingSpeed.Sidereal, Arg.Any<CancellationToken>());
        await mount.Received(1).BeginSlewRaDecAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_ForSun_UsesSolarTrackingAndAnnotatesMessage()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar });

        var (post, msg) = await InvokeAsync(mount, "Sun", CatalogIndex.Sol);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to Sun (solar tracking)");
        await mount.Received(1).EnsureTrackingAsync(TrackingSpeed.Solar, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_ForMoon_UsesLunarTrackingAndAnnotatesMessage()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar });

        var (post, msg) = await InvokeAsync(mount, "Moon", CatalogIndex.Moon);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to Moon (lunar tracking)");
        await mount.Received(1).EnsureTrackingAsync(TrackingSpeed.Lunar, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenMountLacksSolarSpeed_FallsBackToSidereal()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal, TrackingSpeed.Lunar });

        var (post, msg) = await InvokeAsync(mount, "Sun", CatalogIndex.Sol);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to Sun (tracking at sidereal \u2014 mount does not support Solar)");
        await mount.Received(1).EnsureTrackingAsync(TrackingSpeed.Sidereal, Arg.Any<CancellationToken>());
        await mount.DidNotReceive().EnsureTrackingAsync(TrackingSpeed.Solar, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenBelowHorizon_ReturnsTargetBelowHorizonLimit()
    {
        // Transform stub returns Alt=5° — below our 20° horizon limit.
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, altDeg: 5.0);

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem, minAltitude: 20);

        post.ShouldBe(SlewPostCondition.TargetBelowHorizonLimit);
        msg.ShouldBe("M31 is below the horizon limit (20\u00B0)");
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenDestinationSideOfPierUnknown_ReturnsSlewNotPossible()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal });
        mount.DestinationSideOfPierAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(PointingState.Unknown));

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.SlewNotPossible);
        msg.ShouldContain("destination pier side");
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenTransformReturnsNull_ReturnsSlewNotPossibleWithContext()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal });
        mount.TryTransformJ2000ToMountNativeAsync(
                Arg.Any<TianWen.Lib.Astrometry.SOFA.Transform>(),
                Arg.Any<double>(), Arg.Any<double>(), false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<(double, double, double, double)?>(null));

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.SlewNotPossible);
        msg.ShouldStartWith("Cannot slew to M31");
        msg.ShouldContain("transform failed");
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenEnsureTrackingThrows_ContinuesSlewAndAnnotates()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal });
        mount.EnsureTrackingAsync(Arg.Any<TrackingSpeed>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<bool>(new InvalidOperationException("COM error")));

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to M31 (tracking not set \u2014 check mount state)");
        await mount.Received(1).BeginSlewRaDecAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    // ── Park / Unpark handling ──

    [Fact]
    public async Task SlewToJ2000Async_WhenParkedAndCanUnpark_AutoUnparksAndAnnotates()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, canPark: true, canUnpark: true, atPark: true);

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.Slewing);
        msg.ShouldBe("Slewing to M31 (auto-unparked)");
        await mount.Received(1).UnparkAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenNotParkedAndCanUnpark_DoesNotCallUnpark()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, canPark: true, canUnpark: true, atPark: false);

        await InvokeAsync(mount, "M31", NonSolarSystem);

        await mount.DidNotReceive().UnparkAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenCannotUnpark_SkipsAtParkCheck()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, canPark: false, canUnpark: false);

        await InvokeAsync(mount, "M31", NonSolarSystem);

        await mount.DidNotReceive().AtParkAsync(Arg.Any<CancellationToken>());
        await mount.DidNotReceive().UnparkAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenNoParkOrUnparkSupport_AppendsInformationalNote()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, canPark: false, canUnpark: false);

        var (_, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        msg.ShouldBe("Slewing to M31 (mount has no park/unpark support)");
    }

    [Fact]
    public async Task SlewToJ2000Async_WhenUnparkThrows_ReturnsSlewNotPossibleWithContext()
    {
        var mount = SetupMount(new[] { TrackingSpeed.Sidereal }, canPark: true, canUnpark: true, atPark: true);
        mount.UnparkAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new InvalidOperationException("mount refused unpark")));

        var (post, msg) = await InvokeAsync(mount, "M31", NonSolarSystem);

        post.ShouldBe(SlewPostCondition.SlewNotPossible);
        msg.ShouldContain("parked");
        msg.ShouldContain("mount refused unpark");
        await mount.DidNotReceiveWithAnyArgs().BeginSlewRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    // ── Helpers ──

    private static Task<(SlewPostCondition Post, string StatusMessage)> InvokeAsync(
        IMountDriver mount, string name, CatalogIndex? index, int minAltitude = 10)
    {
        var profile = MakeProfile();
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        return MountActions.SlewToJ2000Async(
            mount, name, M31_RA_J2000, M31_DEC_J2000, index,
            profile: profile, timeProvider: timeProvider,
            minAboveHorizonDegrees: minAltitude,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Profile MakeProfile()
    {
        // Realistic site (Melbourne / Mount Burnett-ish) so transform math stays sane.
        // Mount URI is non-"none" so TransformFactory.FromProfile accepts the profile.
        var data = ProfileData.Empty with
        {
            Mount = new Uri("ascom://GSServer"),
            SiteLatitude = -37.877,
            SiteLongitude = 145.178,
            SiteElevation = 120.0
        };
        return new Profile(Guid.NewGuid(), "Test", data);
    }

    // ── SolveAndSyncAsync ──

    [Fact]
    public async Task SolveAndSyncAsync_WhenMountCannotSync_ReturnsNotPossibleWithoutCapture()
    {
        var mount = SetupSyncMount(canSync: false);
        var camera = SetupCamera(MakeSolveImage());
        var solver = Substitute.For<IPlateSolverFactory>();

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.NotPossible);
        outcome.StatusMessage.ShouldContain("does not support sync");
        outcome.CapturedImage.ShouldBeNull();
        await camera.DidNotReceiveWithAnyArgs().StartExposureAsync(default, default, Arg.Any<CancellationToken>());
        await mount.DidNotReceiveWithAnyArgs().SyncRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenCameraNotConnected_ReturnsNotPossible()
    {
        var mount = SetupSyncMount(believed: (6.0, 45.0));
        var camera = Substitute.For<ICameraDriver>();
        camera.Connected.Returns(false);
        var solver = Substitute.For<IPlateSolverFactory>();

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.NotPossible);
        outcome.StatusMessage.ShouldBe("Camera is not connected");
        await mount.DidNotReceiveWithAnyArgs().SyncRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenPointingUnavailable_ReturnsNotPossible()
    {
        // GetRaDecJ2000Async unstubbed -> null: no believed pointing, no search origin.
        var mount = SetupSyncMount(believed: null);
        var camera = SetupCamera(MakeSolveImage());
        var solver = Substitute.For<IPlateSolverFactory>();

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.NotPossible);
        outcome.StatusMessage.ShouldBe("Mount pointing unavailable");
        await camera.DidNotReceiveWithAnyArgs().StartExposureAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenCaptureReturnsNoFrame_ReturnsCaptureFailed()
    {
        var mount = SetupSyncMount(believed: (6.0, 45.0));
        var camera = SetupCamera(image: null);
        var solver = Substitute.For<IPlateSolverFactory>();

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.CaptureFailed);
        outcome.CapturedImage.ShouldBeNull();
        await mount.DidNotReceiveWithAnyArgs().SyncRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenSolveFails_ReturnsSolveFailedWithCapturedImage()
    {
        var mount = SetupSyncMount(believed: (6.0, 45.0));
        var image = MakeSolveImage();
        var camera = SetupCamera(image);
        var solver = SetupSolver(solution: null);

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.SolveFailed);
        // Ownership of the captured frame still transfers so the caller can show/release it.
        outcome.CapturedImage.ShouldBeSameAs(image);
        outcome.SolveResult.ShouldNotBeNull();
        await mount.DidNotReceiveWithAnyArgs().SyncRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_HappyPath_SyncsMountToSolvedNativePosition()
    {
        // Believed (6.0, 45.0) vs solved (6.05, 44.8): a ~46' cone error to sync away.
        var mount = SetupSyncMount(believed: (6.0, 45.0), native: (6.052, 44.82, 180.0, 55.0));
        var image = MakeSolveImage();
        var camera = SetupCamera(image);
        var solver = SetupSolver(solution: new WCS(6.05, 44.8));

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.Synced);
        outcome.StatusMessage.ShouldStartWith("Synced");
        outcome.CapturedImage.ShouldBeSameAs(image);
        outcome.SolveResult.ShouldNotBeNull();
        // Synced with the J2000->native CONVERTED coordinates, not raw J2000.
        await mount.Received(1).SyncRaDecAsync(6.052, 44.82, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WithoutSlewSupport_StillSyncs()
    {
        // The SkyGuider Pro case: CanSlew=false, CanSync=true. Solve & sync is the ONLY
        // way a slew-less tracker's reported position can ever be made truthful.
        var mount = SetupSyncMount(believed: (6.0, 45.0), native: (6.01, 44.99, 180.0, 55.0));
        mount.CanSlew.Returns(false);
        mount.CanSlewAsync.Returns(false);
        var camera = SetupCamera(MakeSolveImage());
        var solver = SetupSolver(solution: new WCS(6.01, 45.0));

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.Synced);
        await mount.Received(1).SyncRaDecAsync(6.01, 44.99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenSyncThrows_ReturnsSyncFailed()
    {
        var mount = SetupSyncMount(believed: (6.0, 45.0), native: (6.05, 44.8, 180.0, 55.0));
        mount.SyncRaDecAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new InvalidOperationException("sync rejected")));
        var image = MakeSolveImage();
        var camera = SetupCamera(image);
        var solver = SetupSolver(solution: new WCS(6.05, 44.8));

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.SyncFailed);
        outcome.StatusMessage.ShouldContain("sync rejected");
        outcome.CapturedImage.ShouldBeSameAs(image);
    }

    [Fact]
    public async Task SolveAndSyncAsync_WhenNativeTransformFails_ReturnsSyncFailedWithoutSyncing()
    {
        var mount = SetupSyncMount(believed: (6.0, 45.0), native: null);
        var camera = SetupCamera(MakeSolveImage());
        var solver = SetupSolver(solution: new WCS(6.05, 44.8));

        var outcome = await InvokeSolveSyncAsync(mount, camera, solver);

        outcome.Result.ShouldBe(MountActions.SolveSyncResult.SyncFailed);
        outcome.StatusMessage.ShouldContain("transform failed");
        await mount.DidNotReceiveWithAnyArgs().SyncRaDecAsync(default, default, Arg.Any<CancellationToken>());
    }

    private static Task<MountActions.SolveSyncOutcome> InvokeSolveSyncAsync(
        IMountDriver mount, ICameraDriver camera, IPlateSolverFactory solver)
    {
        var profile = MakeProfile();
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        return MountActions.SolveAndSyncAsync(
            mount, camera, otaName: "TestOTA", focalLengthMm: 400, apertureMm: 80,
            focuser: null, filterWheel: null, catalogDb: null,
            solverFactory: solver, profile: profile, timeProvider: timeProvider,
            exposure: TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static IMountDriver SetupSyncMount(
        bool canSync = true,
        (double RaJ2000, double DecJ2000)? believed = null,
        (double RaMount, double DecMount, double Az, double Alt)? native = null)
    {
        var mount = Substitute.For<IMountDriver>();
        mount.Connected.Returns(true);
        mount.CanSync.Returns(canSync);
        mount.EquatorialSystem.Returns(EquatorialCoordinateType.Topocentric);
        mount.GetRaDecJ2000Async(
                Arg.Any<TianWen.Lib.Astrometry.SOFA.Transform>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(believed));
        mount.TryTransformJ2000ToMountNativeAsync(
                Arg.Any<TianWen.Lib.Astrometry.SOFA.Transform>(),
                Arg.Any<double>(), Arg.Any<double>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(native));
        return mount;
    }

    private static ICameraDriver SetupCamera(Image? image)
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.Connected.Returns(true);
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetImageAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(image));
        return camera;
    }

    private static IPlateSolverFactory SetupSolver(WCS? solution)
    {
        var solver = Substitute.For<IPlateSolverFactory>();
        solver.SolveImageAsync(
                Arg.Any<Image>(), Arg.Any<ImageDim?>(), Arg.Any<float>(),
                Arg.Any<WCS?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PlateSolveResult(solution, TimeSpan.FromMilliseconds(100))));
        return solver;
    }

    private static Image MakeSolveImage()
    {
        var data = new float[8, 8];
        var meta = new ImageMeta("solveSync", new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromSeconds(1), FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, 1f, 0f, 0f, meta);
    }

    private static IMountDriver SetupMount(
        TrackingSpeed[] supportedSpeeds,
        bool canPark = true, bool canUnpark = true, bool atPark = false,
        double altDeg = 45.0)
    {
        var mount = Substitute.For<IMountDriver>();
        mount.Connected.Returns(true);
        mount.CanSlewAsync.Returns(true);
        mount.CanPark.Returns(canPark);
        mount.CanUnpark.Returns(canUnpark);
        mount.AtParkAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(atPark));
        mount.TrackingSpeeds.Returns(new List<TrackingSpeed>(supportedSpeeds));
        mount.EquatorialSystem.Returns(EquatorialCoordinateType.Topocentric);
        mount.EnsureTrackingAsync(Arg.Any<TrackingSpeed>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));
        // Skip the real SOFA transform — return plausible native coords + the requested alt.
        mount.TryTransformJ2000ToMountNativeAsync(
                Arg.Any<TianWen.Lib.Astrometry.SOFA.Transform>(),
                Arg.Any<double>(), Arg.Any<double>(), false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<(double RaMount, double DecMount, double Az, double Alt)?>(
                (M31_RA_J2000, M31_DEC_J2000, 180.0, altDeg)));
        mount.DestinationSideOfPierAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(PointingState.Normal));
        mount.BeginSlewRaDecAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
        return mount;
    }
}
