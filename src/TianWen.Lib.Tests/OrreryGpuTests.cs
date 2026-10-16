using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Offscreen Vulkan stack the size of <see cref="OrreryTests"/>' view, for <see cref="OrreryGpuTests"/>.</summary>
public sealed class VkOrreryGpuFixture : OffscreenGpuFixtureBase
{
    public VkOrreryGpuFixture() : base(OrreryTests.Width, OrreryTests.Height) { }
}

/// <summary>
/// The orrery through the GPU host the GUI runs (<see cref="VkSkyMapTab"/>; docs/plans/atlas-planet-detail.md, A5, "The bodies on the
/// GPU"): every body ray-traced in the camera's frame through <c>DrawPlanetBody</c>, so Play builds NO sprite, where the CPU surface,
/// which keeps the sprites, builds them by the hundred; and every disc lit toward the Sun on the GPU's own frame, after a run of
/// Play-sized steps as at rest.
/// </summary>
[Collection("UI")]
public sealed class OrreryGpuTests(VkOrreryGpuFixture gpu, ITestOutputHelper output) : IClassFixture<VkOrreryGpuFixture>
{
    private const int Width = OrreryTests.Width;
    private const int Height = OrreryTests.Height;

    private sealed record Rig(VkSkyMapTab Tab, PlannerState Planner, FakeTimeProviderWrapper Time);

    private async Task<Rig> RigAsync()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = OrreryTests.Instant,
        };
        var tab = gpu.Invoke(() => new VkSkyMapTab(gpu.Renderer.ShouldNotBeNull()) { FontPath = FontResolver.ResolveSystemFont(), DpiScale = 1f });
        var rig = new Rig(tab, planner, new FakeTimeProviderWrapper(OrreryTests.Instant));
        Render(rig);
        rig.Tab.State.TryGetPlanetCached(CatalogIndex.Sol, OrreryTests.Instant, out var marker).ShouldBeTrue();
        var site = TianWen.Lib.Astrometry.SOFA.SiteContext.Create(48.0, 11.0, OrreryTests.Instant);
        rig.Tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(db, marker, 48.0, 11.0, OrreryTests.Instant, site);
        rig.Tab.OpenOrrery();
        // The stars behind are the sky pipeline's, measured by OrreryBackdropGpuTests; here only the bodies are judged.
        rig.Tab.State.Orrery.ShowBackdrop = false;
        rig.Tab.State.Orrery.ElevationDeg = 80;
        return rig;
    }

    private byte[] Render(Rig rig) => gpu.Invoke(() =>
    {
        var renderer = gpu.Renderer.ShouldNotBeNull();
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        rig.Tab.Render(rig.Planner, new RectF32(0, 0, Width, Height), rig.Time);
        renderer.EndOffscreenFrame();
        return gpu.Ctx.ShouldNotBeNull().ReadbackOffscreenRgba();
    });

    // The atlas decoded and every body's map uploaded: two frames after the atlas lands (the first queues the uploads).
    private async Task SettleAsync(Rig rig)
    {
        Render(rig);
        if (rig.Tab.PlanetTextures.AtlasLoad is { } atlas)
        {
            await atlas.WaitAsync(TestContext.Current.CancellationToken);
        }
        await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
        Render(rig);
        Render(rig);
    }

    /// <summary>
    /// Play builds no sprite on the GPU host: forty played frames, a hundred milliseconds of the wall clock each (a day of the atlas's
    /// clock), every body traced every frame, each map uploaded once. The CPU surface, which keeps the sprites, is counted beside it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PlayBuildsNoSprite()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var rig = await RigAsync();
        try
        {
            await SettleAsync(rig);
            var uploads = rig.Tab.PlanetBodies.ShouldNotBeNull().Uploads;
            var traced = rig.Tab.PlanetBodiesTraced;
            rig.Tab.State.Orrery.Playing = true;
            const int Frames = 40;
            for (var frame = 0; frame < Frames; frame++)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(100));
                Render(rig);
            }
            var tracedPerFrame = (rig.Tab.PlanetBodiesTraced - traced) / (double)Frames;
            // Four seconds of the wall clock: three of them at the speed in force is a clock that ran.
            rig.Tab.State.TimeOffset.TotalDays.ShouldBeGreaterThan(3 * rig.Tab.State.Orrery.DaysPerSecond, "Play did not run the clock");

            using var cpu = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
            OrreryTests.Select(cpu, CatalogIndex.Sol);
            cpu.Tab.OpenOrrery();
            cpu.Tab.State.Orrery.ShowBackdrop = false;
            cpu.Tab.State.Orrery.ElevationDeg = 80;
            await OrreryTests.SettleAsync(cpu);
            var cpuBefore = cpu.Tab.OrrerySprites.Builds;
            var cpuTime = new FakeTimeProviderWrapper(OrreryTests.Instant);
            cpu.Tab.State.Orrery.Playing = true;
            for (var frame = 0; frame < Frames; frame++)
            {
                cpuTime.Advance(TimeSpan.FromMilliseconds(100));
                cpu.Tab.Render(cpu.Planner, cpu.Content, cpuTime);
                await cpu.Tab.OrrerySprites.WhenBuiltAsync();
            }

            output.WriteLine($"GPU host: {rig.Tab.OrrerySprites.Builds} sprite builds over {Frames} played frames, {tracedPerFrame:F1} bodies traced a frame, "
                + $"{rig.Tab.PlanetBodies.Uploads - uploads} map uploads while playing ({uploads} before); "
                + $"the CPU surface: {cpu.Tab.OrrerySprites.Builds - cpuBefore} sprite builds over the same {Frames} frames");
            rig.Tab.OrrerySprites.Builds.ShouldBe(0);
            tracedPerFrame.ShouldBeGreaterThanOrEqualTo(6, "the planets were not traced every frame");
            rig.Tab.PlanetBodies.Uploads.ShouldBe(uploads, "a played frame uploaded a map again");
        }
        finally
        {
            gpu.Invoke(() =>
            {
                rig.Tab.Dispose();
                return 0;
            });
        }
    }

    /// <summary>
    /// Every planet's disc on the GPU's frame is lit toward the Sun as drawn (<see cref="OrreryTests.LitAwayFromTheSun"/>), at the instant
    /// and after 93 weeks of Play-sized steps: no stale picture is ever drawn, so no frame is a frame behind. Framed on the Earth's orbit
    /// (the view the orrery opens on for the Sun) and out to a reach of 12 AU, where Jupiter and Saturn are in the view too.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(0, 0.0)]
    [InlineData((93 * 7) + 1, 0.0)]
    [InlineData(0, 12.0)]
    [InlineData((93 * 7) + 1, 12.0)]
    public async Task EveryDiscIsLitTowardTheSun(int days, double reachAu)
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var rig = await RigAsync();
        try
        {
            await SettleAsync(rig);
            if (reachAu > 0)
            {
                // After the first frame, which frames the view the orrery opens on.
                rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(reachAu);
            }
            byte[] frame = [];
            for (var step = 0; step <= 20; step++)
            {
                rig.Tab.State.TimeOffset = TimeSpan.FromDays(days * step / 20.0);
                frame = Render(rig);
            }
            rig.Tab.OrrerySprites.Builds.ShouldBe(0);
            var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryGpuTests));
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"orrery-gpu-{days}d-{reachAu:F0}au.png"), PngWriter.Encode(frame, Width, Height), TestContext.Current.CancellationToken);
            output.WriteLine(folder);
            OrreryTests.LitAwayFromTheSun(rig.Tab.OrreryDrawn, frame, Width, Height, output).ShouldBeEmpty();
            if (reachAu > 0)
            {
                // The giants are what this framing is for: both judged, not skipped as out of the view.
                foreach (var giant in new[] { CatalogIndex.Jupiter, CatalogIndex.Saturn })
                {
                    var d = rig.Tab.OrreryDrawn.Single(b => b.Index == giant);
                    (d.X - d.Radius >= 0 && d.Y - d.Radius >= 0 && d.X + d.Radius <= Width && d.Y + d.Radius <= Height && d.Radius >= 4)
                        .ShouldBeTrue($"{giant} at ({d.X:F0}, {d.Y:F0}), r {d.Radius:F1}, is not judged");
                }
            }
        }
        finally
        {
            gpu.Invoke(() =>
            {
                rig.Tab.Dispose();
                return 0;
            });
        }
    }
}
