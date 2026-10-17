using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The shipped Milky Way texture through the real sky map pipeline, on a real device: the Large Magellanic Cloud must
/// shine at the view's centre when the view is pointed at it, against the dark sky around it. The texture is NASA's
/// starless map (<see cref="MilkyWaySvsImport"/>), whose own layout runs right ascension the other way; a mirror missed
/// in the import, or a shader that maps the texture the wrong way round, puts the Cloud anywhere but the centre.
/// </summary>
/// <remarks>
/// <para>Skipped without Vulkan (CI has none) and without the texture itself: it is a Git LFS object, so a checkout
/// that did not pull it holds a pointer of a few hundred bytes, which this reads as absent rather than failing to
/// decode.</para>
/// <para>It tells the textures apart: run against the Tycho-2 bake it replaced (2026-10-12, on an Adreno X1-85), the
/// Cloud is a smudge that does not clear the sky round it, and the test fails; NASA's map passes.</para>
/// <para>Set <c>MILKYWAY_PROBE_DIR</c> to also write the frames as PNG, the Cloud's and the galactic centre's and
/// Cygnus's; <c>MILKYWAY_LZ</c> renders another texture file instead of the shipped one, so two textures can be put
/// side by side through the same pipeline.</para>
/// </remarks>
[Collection(GpuCollection.Name)]
public sealed class SkyMapMilkyWayGpuTests(VkSkyMapGpuFixture gpu) : IClassFixture<VkSkyMapGpuFixture>
{
    private const int Width = VkSkyMapGpuFixture.Width;
    private const int Height = VkSkyMapGpuFixture.Height;

    // The horizon tests' site and evening, so the one star buffer the fixture builds serves both.
    private const double SiteLatitude = -33.9;
    private const double SiteLongitude = 18.4;
    private static readonly DateTimeOffset When = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    // The Large Magellanic Cloud's centre (J2000).
    private const double LmcRaHours = 5.392;
    private const double LmcDecDeg = -69.76;

    [Fact]
    public async Task TheLargeMagellanicCloudShinesWhereTheSkyPutsIt()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        if (LoadTexture() is not { } texture)
        {
            Assert.Skip("the Milky Way texture is not in this checkout (an LFS pointer, or no source tree)");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var site = SiteContext.Create(SiteLatitude, SiteLongitude, When);
        var pipeline = await gpu.GetStarPipelineAsync(db, When, ct);
        gpu.Invoke(() =>
        {
            pipeline.LoadMilkyWayTexture(texture.Bgra, texture.Width, texture.Height);
            return 0;
        });

        var lmc = RenderAt(pipeline, LmcRaHours, LmcDecDeg, site);
        Dump(lmc, "lmc");
        Dump(RenderAt(pipeline, 17.761, -29.0, site), "galactic-centre");
        Dump(RenderAt(pipeline, 20.6, 42.0, site), "cygnus");

        // Medians, so the stars drawn over the texture do not decide either number: the Cloud spans about ten degrees,
        // so a disc of three degrees round the centre is all Cloud, and the corners of a 30 degree field are sky.
        var centre = MedianLuminance(lmc, Width / 2, Height / 2, Width / 20);
        var corners = Math.Max(
            Math.Max(MedianLuminance(lmc, Width / 10, Height / 10, Width / 20),
                MedianLuminance(lmc, Width - (Width / 10), Height / 10, Width / 20)),
            Math.Max(MedianLuminance(lmc, Width / 10, Height - (Height / 10), Width / 20),
                MedianLuminance(lmc, Width - (Width / 10), Height - (Height / 10), Width / 20)));

        centre.ShouldBeGreaterThan(8, "the Cloud is drawn where the view points; a frame with no texture reads 0");
        centre.ShouldBeGreaterThan(2 * Math.Max(corners, 1),
            $"the Cloud at the centre ({centre}) shines against the sky round it (brightest corner {corners})");
    }

    private static MilkyWayTextureFile? LoadTexture()
    {
        var path = Environment.GetEnvironmentVariable("MILKYWAY_LZ") is { Length: > 0 } given
            ? given
            : ChromeMeasuresThroughTheEngineTests.FindSourceRoot() is { } root
                ? Path.Combine(root, "TianWen.UI.Gui", "Resources", "milkyway.bgra.lz")
                : null;

        // An LFS pointer is a few hundred bytes of text; the texture is megabytes of lzip.
        return path is not null && File.Exists(path) && new FileInfo(path).Length > 4096
            ? MilkyWayTextureFile.Decode(File.ReadAllBytes(path))
            : null;
    }

    /// <summary>One offscreen frame, 30 degrees across, centred on a point of the sky, the Milky Way at full strength.</summary>
    private byte[] RenderAt(VkSkyMapPipeline pipeline, double centerRaHours, double centerDecDeg, SiteContext site)
    {
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Horizon,
            CenterRA = centerRaHours,
            CenterDec = centerDecDeg,
            FieldOfViewDeg = 30.0,
            ShowHorizon = false,
            ShowMilkyWay = true,
            ShowAltAzGrid = false,
            ShowGrid = false,
        };

        return gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx!;
            var renderer = gpu.Renderer!;

            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            pipeline.UpdateUbo(state, Width, Height, 0f, 0f, site, ctx.CurrentFrame);
            pipeline.Draw(
                renderer.CurrentCommandBuffer, state, Width, Height, 0f, 0f, 1f,
                default, default, default, phase: SkyMapDrawPhase.Backdrop);
            renderer.EndOffscreenFrame();
            return ctx.ReadbackOffscreenRgba();
        });
    }

    /// <summary>The median of (R + G + B) / 3 over a square of half-side <paramref name="radius"/>.</summary>
    private static int MedianLuminance(byte[] rgba, int cx, int cy, int radius)
    {
        var values = new int[(2 * radius + 1) * (2 * radius + 1)];
        var n = 0;
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                var i = (y * Width + x) * 4;
                values[n++] = (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3;
            }
        }

        Array.Sort(values, 0, n);
        return values[n / 2];
    }

    private static void Dump(byte[] rgba, string name)
    {
        if (Environment.GetEnvironmentVariable("MILKYWAY_PROBE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        var rgb = new byte[Width * Height * 3];
        for (int p = 0, q = 0; p < rgba.Length; p += 4, q += 3)
        {
            rgb[q] = rgba[p];
            rgb[q + 1] = rgba[p + 1];
            rgb[q + 2] = rgba[p + 2];
        }

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"milkyway-{name}.png"), PngWriter.EncodeRgb8(rgb, Width, Height));
    }
}
