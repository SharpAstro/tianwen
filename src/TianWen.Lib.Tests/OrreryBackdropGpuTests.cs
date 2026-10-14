using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The orrery's backdrop on a real device, through the sky map's own Vulkan star pipeline (docs/plans/atlas-planet-detail.md, A5,
/// "The backdrop"): with <see cref="SkyMapState.Backdrop"/> set, the shaders project gnomonically through the orrery camera's
/// rotation and the obliquity, so Sirius is drawn where the camera's own math puts it; and Night's tint leaves no blue in the frame.
/// The CPU surface's counterpart is <see cref="OrreryTests"/>; this is the half only a GPU can answer, and it writes the GPU's picture.
/// </summary>
[Collection("UI")]
public sealed class OrreryBackdropGpuTests(VkSkyMapGpuFixture gpu, ITestOutputHelper output) : IClassFixture<VkSkyMapGpuFixture>
{
    private const int Width = VkSkyMapGpuFixture.Width;
    private const int Height = VkSkyMapGpuFixture.Height;
    private static readonly DateTimeOffset When = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    // Sirius, J2000.
    private const double SiriusRa = 6.752478;
    private const double SiriusDec = -16.716116;

    [Fact]
    public async Task TheGpuBackdropDrawsSiriusWhereTheCameraPutsItAndNightSpendsNoBlue()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var pipeline = await gpu.GetStarPipelineAsync(db, When, ct);

        // The camera faces Sirius eighteen degrees off, where the gnomonic and the stereographic projections part by about 5 px.
        Span<double> dir = [Math.Cos(SiriusDec * Math.PI / 180) * Math.Cos(SiriusRa * Math.PI / 12), Math.Cos(SiriusDec * Math.PI / 180) * Math.Sin(SiriusRa * Math.PI / 12), Math.Sin(SiriusDec * Math.PI / 180)];
        SolarSystemOrbits.EquatorialToEcliptic(dir);
        var rect = new RectF32(0, 0, Width, Height);
        var camera = new OrreryCamera((Math.Atan2(-dir[1], -dir[0]) * 180 / Math.PI) + 18, -Math.Asin(dir[2]) * 180 / Math.PI, 6, rect);
        var view = camera.EquatorialView(out var axis);
        SkyMapProjection.ProjectGnomonicWithMatrix(SiriusRa, SiriusDec, view, camera.FocalPx, Width / 2f, Height / 2f, out var px, out var py).ShouldBeTrue();

        var plain = Render(pipeline, new SkyOrreryBackdrop(view, camera.FocalPx, axis, 90, OrreryCamera.FieldOfViewDeg, 0f, 1f, null));
        // The bright core's centroid (every channel's maximum above 200) within 15 px of the prediction.
        double sumX = 0, sumY = 0, sumW = 0;
        for (var y = (int)py - 15; y <= (int)py + 15; y++)
        {
            for (var x = (int)px - 15; x <= (int)px + 15; x++)
            {
                if (x >= 0 && x < Width && y >= 0 && y < Height)
                {
                    var i = ((y * Width) + x) * 4;
                    var level = Math.Max(plain[i], Math.Max(plain[i + 1], plain[i + 2]));
                    if (level > 200)
                    {
                        sumX += (x + 0.5) * level;
                        sumY += (y + 0.5) * level;
                        sumW += level;
                    }
                }
            }
        }
        sumW.ShouldBeGreaterThan(0, "the GPU drew no bright star near where the camera's math puts Sirius");
        var (cx, cy) = (sumX / sumW, sumY / sumW);
        output.WriteLine($"Sirius predicted at ({px:F1}, {py:F1}); the drawn core's centroid ({cx:F1}, {cy:F1})");
        cx.ShouldBe(px, 1.5);
        cy.ShouldBe(py, 1.5);

        var night = Render(pipeline, new SkyOrreryBackdrop(view, camera.FocalPx, axis, 90, OrreryCamera.FieldOfViewDeg, 0f, 1f, new RGBAColor32(0xE0, 0x30, 0x00, 0xFF)));
        var lit = 0;
        var blue = 0;
        for (var i = 0; i < night.Length; i += 4)
        {
            if (night[i] > 40)
            {
                lit++;
            }
            if (night[i + 2] != 0)
            {
                blue++;
            }
        }
        output.WriteLine($"Night: {lit} lit pixels, {blue} with any blue");
        lit.ShouldBeGreaterThan(50, "the Night frame drew no stars");
        blue.ShouldBe(0);

        var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryBackdropGpuTests));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "orrery-backdrop-gpu.png"), PngWriter.Encode(plain, Width, Height), ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, "orrery-backdrop-gpu-night.png"), PngWriter.Encode(night, Width, Height), ct);
        output.WriteLine(folder);
    }

    /// <summary>
    /// The Milky Way behind the orrery is drawn at the orrery's own Milky Way opacity, NOT further dimmed by the stars' brightness
    /// factor: a plain white texture through the real pipeline reads <see cref="SkyMapTab{TSurface}.OrreryMilkyWayAlpha"/> of full
    /// scale, and at least half. It read 0.35 x 0.2 = 0.07 of it, which the owner saw as a backdrop "super dim".
    /// </summary>
    [Fact]
    public async Task TheMilkyWayIsDrawnAtTheOrrerysOwnOpacity()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var pipeline = await gpu.GetStarPipelineAsync(db, When, ct);
        var white = new byte[64 * 32 * 4];
        Array.Fill(white, (byte)0xFF);
        gpu.Invoke(() =>
        {
            pipeline.LoadMilkyWayTexture(white, 64, 32);
            return 0;
        });

        var camera = new OrreryCamera(-90, 30, 6, new RectF32(0, 0, Width, Height));
        var view = camera.EquatorialView(out var axis);
        var frame = Render(pipeline, new SkyOrreryBackdrop(view, camera.FocalPx, axis, 90, OrreryCamera.FieldOfViewDeg,
            SkyMapTab<RgbaImage>.OrreryMilkyWayAlpha, SkyMapTab<RgbaImage>.OrreryBackdropBrightness, null), milkyWay: true);

        var reds = new byte[Width * Height];
        for (var i = 0; i < reds.Length; i++)
        {
            reds[i] = frame[i * 4];
        }
        Array.Sort(reds);
        var median = reds[reds.Length / 2];
        output.WriteLine($"the Milky Way's median level {median} of 255, for an opacity of {SkyMapTab<RgbaImage>.OrreryMilkyWayAlpha}");
        ((double)median).ShouldBe(SkyMapTab<RgbaImage>.OrreryMilkyWayAlpha * 255.0, 8.0);
        ((int)median).ShouldBeGreaterThanOrEqualTo(128);
    }

    private byte[] Render(VkSkyMapPipeline pipeline, SkyOrreryBackdrop backdrop, bool milkyWay = false)
    {
        var state = new SkyMapState { ShowHorizon = false, ShowMilkyWay = milkyWay, ShowAltAzGrid = false, ShowGrid = false };
        state.Backdrop = backdrop;
        var site = SiteContext.Create(48.0, 11.0, When);
        return gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx!;
            var renderer = gpu.Renderer!;
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            pipeline.UpdateUbo(state, Width, Height, 0f, 0f, site, ctx.CurrentFrame);
            pipeline.Draw(renderer.CurrentCommandBuffer, state, Width, Height, 0f, 0f, milkyWay ? backdrop.MilkyWayAlpha : 0f,
                default, default, default, phase: SkyMapDrawPhase.Backdrop);
            renderer.EndOffscreenFrame();
            return ctx.ReadbackOffscreenRgba();
        });
    }
}
