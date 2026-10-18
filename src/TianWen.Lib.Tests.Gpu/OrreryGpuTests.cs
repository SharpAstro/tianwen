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
[Collection(GpuCollection.Name)]
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
        // The Sun's panel opens with the view (#1656) and covers the frame's bottom left, where a disc or a comet's head is
        // judged: closed, as only the bodies are under test here.
        rig.Tab.State.Orrery.Selected = null;
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
        await rig.Tab.OrreryScene.WhenCometsRankedAsync();
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
    /// The comets on the GPU host (#1611): the set's heads, orbits and tails are 2D primitives (<c>FillEllipse</c>, <c>DrawPolyline</c>,
    /// <c>DrawTriangles</c>) the Vulkan renderer fills as the CPU surface does. With the layer on, every drawn head and the middle of
    /// every drawn tail differ from the same frame with the layer off; the frame's CPU time with and without the set is reported.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCometsDrawOnTheGpuHost()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var rig = await RigAsync();
        try
        {
            rig.Planner.Comets = new StubCometRepository(OrreryCometTests.DemoComets());
            rig.Tab.State.Orrery.ElevationDeg = 30;
            await SettleAsync(rig);
            double Median()
            {
                var times = new double[20];
                for (var frame = 0; frame < times.Length; frame++)
                {
                    rig.Tab.State.Orrery.AzimuthDeg += 0.05;
                    Render(rig);
                    times[frame] = rig.Tab.LastOrreryFrameMs;
                }
                Array.Sort(times);
                return times[times.Length / 2];
            }
            var withComets = Median();
            var on = Render(rig);
            var heads = rig.Tab.OrreryDrawn.Where(d => d.Index.ToCatalog() == Catalog.Comet && d.X >= 0 && d.Y >= 0 && d.X < Width && d.Y < Height).ToList();
            var tails = rig.Tab.OrreryTails.Where(t => t.HeadX >= 0 && t.HeadY >= 0 && t.TipX >= 0 && t.TipY >= 0 && t.HeadX < Width && t.TipX < Width && t.HeadY < Height && t.TipY < Height).ToList();
            rig.Tab.State.ShowComets = false;
            var withoutComets = Median();
            var off = Render(rig);

            var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryGpuTests));
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "orrery-gpu-comets.png"), PngWriter.Encode(on, Width, Height), TestContext.Current.CancellationToken);
            output.WriteLine(folder);

            bool Differs(float x, float y)
            {
                var i = (((int)y * Width) + (int)x) * 4;
                return Math.Abs(on[i] - off[i]) + Math.Abs(on[i + 1] - off[i + 1]) + Math.Abs(on[i + 2] - off[i + 2]) > 12;
            }
            output.WriteLine($"GPU host: {heads.Count} comet heads and {tails.Count} tails on screen; frame median {withComets:F2} ms with the set, {withoutComets:F2} ms without (CPU time of the frame's build and submit)");
            heads.Count.ShouldBeGreaterThan(5);
            tails.Count.ShouldBeGreaterThan(0);
            heads.Where(h => !Differs(h.X, h.Y)).Select(h => h.Index).ShouldBeEmpty("a comet's head is not on the GPU's frame");
            tails.Where(t => !Differs((t.HeadX + t.TipX) / 2, (t.HeadY + t.TipY) / 2)).Select(t => t.Index)
                .ShouldBeEmpty("a comet's tail is not on the GPU's frame");
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
    /// The planets' orbits are drawn over the backdrop on the GPU host as they are without it. A sample on an orbit is lit when its
    /// pixel is more than 8 levels brighter than both pixels 5 px either side across the orbit; with the backdrop on at least 80 % of
    /// the samples lit with it off are lit (the stars along the paths take a few). This scene read 14 % against 91 % (229 and 1,473 of
    /// 1,625) when the sky pass left the offscreen frame a zero viewport that hid every 2D primitive after it, and 1,396 of 1,625 with
    /// the fix (docs/plans/atlas-planet-detail.md, A5, "The backdrop").
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOrbitsAreDrawnOverTheBackdrop()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }
        var rig = await RigAsync();
        try
        {
            rig.Planner.PlanningDate = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            rig.Tab.State.ShowComets = false;
            rig.Tab.State.Orrery.ElevationDeg = 25;
            await SettleAsync(rig);
            rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(6);
            var off = Render(rig);
            rig.Tab.State.Orrery.ShowBackdrop = true;
            Render(rig);
            var on = Render(rig);
            rig.Tab.LastOrreryBackdrop.ShouldNotBeNull("the backdrop was not drawn");

            var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryGpuTests));
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "orrery-gpu-orbits-backdrop.png"), PngWriter.Encode(on, Width, Height), TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(folder, "orrery-gpu-orbits-plain.png"), PngWriter.Encode(off, Width, Height), TestContext.Current.CancellationToken);
            output.WriteLine(folder);

            var (litOff, litOn, samples) = (0, 0, 0);
            var orbits = rig.Tab.OrreryScene.Orbits.ShouldNotBeNull();
            var camera = new OrreryCamera(rig.Tab.State.Orrery.AzimuthDeg, rig.Tab.State.Orrery.ElevationDeg, rig.Tab.State.Orrery.Distance, new RectF32(0, 0, Width, Height));
            var scaling = new OrreryScaling(rig.Tab.State.Orrery.Scale, OrreryScaling.ReachFor(rig.Tab.State.Orrery.Distance));
            foreach (var (planet, points) in orbits)
            {
                var (planetOff, planetOn, planetSamples) = (0, 0, 0);
                (float X, float Y)? previous = null;
                for (var i = 0; i <= points.Length / 3; i++)
                {
                    var j = i % (points.Length / 3);
                    var (wx, wy, wz) = OrreryScene.Scaled(scaling, points[3 * j], points[(3 * j) + 1], points[(3 * j) + 2]);
                    if (!camera.TryProject(wx, wy, wz, out var sx, out var sy))
                    {
                        previous = null;
                        continue;
                    }
                    if (previous is var (px, py))
                    {
                        // The segment's middle, and the two pixels 5 px either side of it along the segment's normal.
                        var (mx, my) = ((px + sx) / 2, (py + sy) / 2);
                        var (dx, dy) = (sx - px, sy - py);
                        var len = MathF.Sqrt((dx * dx) + (dy * dy));
                        if (len > 0 && mx >= 6 && my >= 6 && mx < Width - 6 && my < Height - 6)
                        {
                            var (nx, ny) = (-dy / len * 5, dx / len * 5);
                            planetSamples++;
                            planetOff += Lit(off, mx, my, nx, ny) ? 1 : 0;
                            planetOn += Lit(on, mx, my, nx, ny) ? 1 : 0;
                        }
                    }
                    previous = (sx, sy);
                }
                output.WriteLine($"{planet}: {planetOn} of {planetSamples} samples lit over the backdrop, {planetOff} without it");
                (litOff, litOn, samples) = (litOff + planetOff, litOn + planetOn, samples + planetSamples);
            }
            output.WriteLine($"all orbits: {litOn} of {samples} samples lit over the backdrop, {litOff} without it");
            litOff.ShouldBeGreaterThan(samples / 2, "the orbits are not drawn even without the backdrop");
            litOn.ShouldBeGreaterThanOrEqualTo(litOff * 8 / 10, "the orbits are not drawn over the backdrop");
        }
        finally
        {
            gpu.Invoke(() =>
            {
                rig.Tab.Dispose();
                return 0;
            });
        }

        static bool Lit(byte[] frame, float x, float y, float nx, float ny)
        {
            static int Level(byte[] f, float x, float y)
            {
                var i = (((int)y * Width) + (int)x) * 4;
                return Math.Max(f[i], Math.Max(f[i + 1], f[i + 2]));
            }
            var centre = Level(frame, x, y);
            return centre > Level(frame, x + nx, y + ny) + 8 && centre > Level(frame, x - nx, y - ny) + 8;
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
