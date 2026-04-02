using System;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class ContrastBoostTests(ITestOutputHelper testOutputHelper)
{
    private const string RGGBImage = "RGGB_frame_bx0_by0_top_down";
    private const string VelaColor = "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop";
    private const string VelaMono = "Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop";

    /// <summary>
    /// Simulates the GPU applyCurve function on CPU.
    /// Must match the GLSL implementation exactly.
    /// SP is placed slightly above the background so the background gets darkened.
    /// Everything above SP gets boosted (midtones/detail). Above HP is passthrough (star protection).
    /// </summary>
    private static float ApplyCurve(float v, float boost, float bg)
    {
        var hp = 0.85f;
        if (v <= 0f || v >= 1f || bg <= 0f || bg >= hp) return v;

        var sp = bg * (1f + 0.1f * boost);
        sp = MathF.Min(sp, hp - 0.01f);

        if (v <= sp)
        {
            var t = v / sp;
            var darkPower = 1f + boost * 3f;
            return sp * MathF.Pow(t, darkPower);
        }
        else if (v < hp)
        {
            var t = (v - sp) / (hp - sp);
            return sp + (hp - sp) * MathF.Pow(t, 1f / (1f + boost));
        }
        else
        {
            return v;
        }
    }

    /// <summary>
    /// Applies the curve to every pixel in the image, returning a new float array.
    /// </summary>
    private static float[][,] ApplyCurveToImage(Image image, float boost, float bg)
    {
        var channels = Image.CreateChannelData(image.ChannelCount, image.Height, image.Width);
        for (var c = 0; c < image.ChannelCount; c++)
        {
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    channels[c][y, x] = ApplyCurve(image[c, y, x], boost, bg);
                }
            }
        }
        return channels;
    }

    /// <summary>
    /// Computes the average pixel value over a square region of a single channel.
    /// </summary>
    private static float AverageRegion(Image image, int channel, int x0, int y0, int size)
    {
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y0 + size && y < image.Height; y++)
        {
            for (var x = x0; x < x0 + size && x < image.Width; x++)
            {
                sum += image[channel, y, x];
                count++;
            }
        }
        return (float)(sum / count);
    }

    /// <summary>
    /// Computes average Rec.709 luminance over a square region.
    /// </summary>
    private static float AverageLuma(Image image, int x0, int y0, int size)
    {
        if (image.ChannelCount < 3)
        {
            return AverageRegion(image, 0, x0, y0, size);
        }
        var r = AverageRegion(image, 0, x0, y0, size);
        var g = AverageRegion(image, 1, x0, y0, size);
        var b = AverageRegion(image, 2, x0, y0, size);
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    [Theory]
    [InlineData(RGGBImage, "AHD", 10, 3)]
    [InlineData(VelaColor, "None", 15, 3)]
    [InlineData(VelaMono, "None", 15, 1)]
    public async Task GivenImage_WhenStretched_BoostDarkensBackgroundAndIncreasesContrast(string imageName, string algorithmStr, int stretchPct, int expectedChannels)
    {
        var algorithm = Enum.Parse<DebayerAlgorithm>(algorithmStr);
        var cancellationToken = TestContext.Current.CancellationToken;
        var rawImage = await SharedTestData.ExtractGZippedFitsImageAsync(imageName, cancellationToken: cancellationToken);
        var testDir = SharedTestData.CreateTempTestOutputDir(nameof(ContrastBoostTests));

        // Stretch — returns normalized [0,1] image
        var stretchFactor = stretchPct * 0.01d;
        var sigma = -5.0;
        var stretched = await rawImage.StretchUnlinkedAsync(stretchFactor, sigma, algorithm, cancellationToken);
        stretched.ChannelCount.ShouldBe(expectedChannels);
        stretched.MaxValue.ShouldBe(1.0f);

        testOutputHelper.WriteLine($"Stretched image: {stretched.Width}x{stretched.Height}x{stretched.ChannelCount}, MaxValue={stretched.MaxValue:F4}");

        // Save stretched (no boost) image
        var shortName = imageName.Length > 30 ? imageName[..30] : imageName;
        var magickBefore = await stretched.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);
        magickBefore.ShouldNotBeNull();
        var beforePath = Path.Combine(testDir, $"{shortName}_f{stretchFactor:F2}_s{sigma:F1}_stretched.tiff");
        await File.WriteAllBytesAsync(beforePath, magickBefore.ToByteArray(MagickFormat.Tiff), cancellationToken);
        testOutputHelper.WriteLine($"Saved stretched image: {beforePath}");

        // Scan for background and object regions
        var squareSize = 32;
        var step = squareSize * 4;

        var minLuma = float.MaxValue;
        int bgX = 0, bgY = 0;
        var maxMidLuma = float.MinValue;
        int objX = 0, objY = 0;

        for (var y = 0; y < stretched.Height - squareSize; y += step)
        {
            for (var x = 0; x < stretched.Width - squareSize; x += step)
            {
                var luma = AverageLuma(stretched, x, y, squareSize);
                if (luma < minLuma && luma > 0.001f)
                {
                    minLuma = luma;
                    bgX = x;
                    bgY = y;
                }
                // Object: brighter than background but not a star
                if (luma > 0.25f && luma < 0.7f && luma > maxMidLuma)
                {
                    maxMidLuma = luma;
                    objX = x;
                    objY = y;
                }
            }
        }

        var bgLuma = AverageLuma(stretched, bgX, bgY, squareSize);
        var objLuma = AverageLuma(stretched, objX, objY, squareSize);

        testOutputHelper.WriteLine($"Background ({bgX},{bgY}): luma = {bgLuma:F6}");
        testOutputHelper.WriteLine($"Object ({objX},{objY}): luma = {objLuma:F6}");

        // The background should be noticeably dimmer than the object
        bgLuma.ShouldBeLessThan(objLuma);

        // Table header
        testOutputHelper.WriteLine($"\n| Boost | BG before | BG after  | BG delta  | Obj before | Obj after  | Obj delta  | Contrast before | Contrast after |");
        testOutputHelper.WriteLine($"|-------|-----------|-----------|-----------|------------|------------|------------|-----------------|----------------|");

        foreach (var boost in ViewerState.CurvesBoostPresets)
        {
            if (boost == 0f) continue;

            var boostedBg = ApplyCurve(bgLuma, boost, bgLuma);
            var boostedObj = ApplyCurve(objLuma, boost, bgLuma);
            var contrastBefore = objLuma - bgLuma;
            var contrastAfter = boostedObj - boostedBg;

            testOutputHelper.WriteLine(
                $"| {boost,5:F2} | {bgLuma,9:F4} | {boostedBg,9:F4} | {boostedBg - bgLuma,+9:F4} | {objLuma,10:F4} | {boostedObj,10:F4} | {boostedObj - objLuma,+10:F4} | {contrastBefore,15:F4} | {contrastAfter,14:F4} |");

            // Background should get DARKER
            boostedBg.ShouldBeLessThan(bgLuma,
                $"Background should get darker with boost={boost}, but went from {bgLuma:F4} to {boostedBg:F4}");

            // Contrast should INCREASE (this is the key requirement)
            contrastAfter.ShouldBeGreaterThan(contrastBefore,
                $"Contrast should increase with boost={boost}: was {contrastBefore:F4}, now {contrastAfter:F4}");

            // Save boosted image
            var boostedData = ApplyCurveToImage(stretched, boost, bgLuma);
            var boostedImage = new Image(boostedData, BitDepth.Float32, 1.0f, 0f, 0f, stretched.ImageMeta);
            var magickBoosted = await boostedImage.ToMagickImageAsync(DebayerAlgorithm.None, cancellationToken);
            magickBoosted.ShouldNotBeNull();
            var boostedPath = Path.Combine(testDir, $"{shortName}_f{stretchFactor:F2}_s{sigma:F1}_boost{boost:F2}.tiff");
            await File.WriteAllBytesAsync(boostedPath, magickBoosted.ToByteArray(MagickFormat.Tiff), cancellationToken);
        }

        testOutputHelper.WriteLine($"\nImages saved to: {testDir}");
    }

    /// <summary>
    /// Verifies that the computed post-stretch background (from measured image data)
    /// matches the actual background of the CPU-stretched image. A mismatch means
    /// the boost curve's symmetry point (SP) is placed at the wrong level.
    /// Uses AstroImageDocument.OpenAsync and ComputeStretchUniforms directly — no duplication.
    /// </summary>
    [Theory]
    // RGGB skipped: AstroImageDocument keeps raw Bayer (1-channel, GPU debayers at render time),
    // so document stretch stats don't match CPU-debayered 3-channel comparison image.
    [InlineData(VelaColor, "None", 15, 3)]
    [InlineData(VelaColor, "None", 10, 3)]
    [InlineData(VelaMono, "None", 15, 1)]
    public async Task ComputePostStretchBackground_MatchesActualBackground(string imageName, string algorithmStr, int stretchPct, int expectedChannels)
    {
        var algorithm = Enum.Parse<DebayerAlgorithm>(algorithmStr);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Use the real AstroImageDocument.OpenAsync — same path as the viewer
        var filePath = await SharedTestData.ExtractGZippedFitsFileAsync(imageName, cancellationToken);
        var document = await AstroImageDocument.OpenAsync(filePath, algorithm, cancellationToken);
        document.ShouldNotBeNull();
        document.UnstretchedImage.ChannelCount.ShouldBe(expectedChannels);

        for (var c = 0; c < document.PerChannelBackground.Length; c++)
        {
            testOutputHelper.WriteLine($"Ch{c} background (pedestal-subtracted): {document.PerChannelBackground[c]:F6}");
        }
        testOutputHelper.WriteLine($"Luma background (pedestal-subtracted): {document.LumaBackground:F6}");

        // Use the real ComputeStretchUniforms — same path as the viewer
        var stretchFactor = stretchPct * 0.01d;
        var sigma = -5.0;
        var stretch = document.ComputeStretchUniforms(StretchMode.Unlinked, new StretchParameters(stretchFactor, sigma));

        testOutputHelper.WriteLine($"\nStretch uniforms (Mode={stretch.Mode}, NormFactor={stretch.NormFactor:F6}):");
        testOutputHelper.WriteLine($"  Pedestal: ({stretch.Pedestal.R:F6}, {stretch.Pedestal.G:F6}, {stretch.Pedestal.B:F6})");
        testOutputHelper.WriteLine($"  Shadows:  ({stretch.Shadows.R:F6}, {stretch.Shadows.G:F6}, {stretch.Shadows.B:F6})");
        testOutputHelper.WriteLine($"  Midtones: ({stretch.Midtones.R:F6}, {stretch.Midtones.G:F6}, {stretch.Midtones.B:F6})");
        testOutputHelper.WriteLine($"  Rescale:  ({stretch.Rescale.R:F6}, {stretch.Rescale.G:F6}, {stretch.Rescale.B:F6})");

        // Computed background — same call as the viewer's renderer
        var computedBg = stretch.ComputePostStretchBackground(document.PerChannelBackground, document.LumaBackground);
        testOutputHelper.WriteLine($"\nComputePostStretchBackground = {computedBg:F6}");

        // CPU stretch and measure actual background
        var rawImage = await SharedTestData.ExtractGZippedFitsImageAsync(imageName, cancellationToken: cancellationToken);
        var stretched = await rawImage.StretchUnlinkedAsync(stretchFactor, sigma, algorithm, cancellationToken);

        var squareSize = 32;
        var step = squareSize * 4;
        var minLuma = float.MaxValue;
        int bgX = 0, bgY = 0;
        for (var y = 0; y < stretched.Height - squareSize; y += step)
        {
            for (var x = 0; x < stretched.Width - squareSize; x += step)
            {
                var luma = AverageLuma(stretched, x, y, squareSize);
                if (luma < minLuma && luma > 0.001f)
                {
                    minLuma = luma;
                    bgX = x;
                    bgY = y;
                }
            }
        }
        var actualBg = AverageLuma(stretched, bgX, bgY, squareSize);

        testOutputHelper.WriteLine($"Actual post-stretch background ({bgX},{bgY}): luma = {actualBg:F6}");
        testOutputHelper.WriteLine($"Estimate vs Actual: {computedBg:F6} vs {actualBg:F6} (ratio={computedBg / actualBg:F4})");

        // The SP the curve would use with each bg value
        var boost = 1.5f;
        var estimatedSp = computedBg * (1f + 0.1f * boost);
        var actualSp = actualBg * (1f + 0.1f * boost);
        testOutputHelper.WriteLine($"\nWith boost={boost:F2}:");
        testOutputHelper.WriteLine($"  Computed SP = {estimatedSp:F6}  (bg pixels {(actualBg <= estimatedSp ? "BELOW" : "ABOVE")} SP → {(actualBg <= estimatedSp ? "darken" : "BOOST!")})");
        testOutputHelper.WriteLine($"  Actual SP    = {actualSp:F6}  (bg pixels BELOW SP → darken)");

        // The estimate should be within 50% of the actual value
        // If not, the curve's SP will be misplaced and boost will malfunction
        var ratio = computedBg / actualBg;
        ratio.ShouldBeInRange(0.5f, 2.0f,
            $"Computed background {computedBg:F4} is too far from actual {actualBg:F4} (ratio={ratio:F4}). " +
            $"This will cause the boost curve to malfunction.");
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    [InlineData(1.50f)]
    public void ApplyCurve_AtHighlight_IsPassthrough(float boost)
    {
        var bg = 0.15f;
        ApplyCurve(0.90f, boost, bg).ShouldBe(0.90f);
        ApplyCurve(0.95f, boost, bg).ShouldBe(0.95f);
        ApplyCurve(1.00f, boost, bg).ShouldBe(1.00f);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    public void ApplyCurve_WellBelowBackground_GetsDarker(float boost)
    {
        var bg = 0.20f;
        var sp = bg * (1f + 0.1f * boost);

        // Value well below SP should get darker
        var val = sp * 0.3f;
        var boosted = ApplyCurve(val, boost, bg);
        boosted.ShouldBeLessThan(val,
            $"Value {val:F4} well below SP should get darker, got {boosted:F4}");
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(1.00f)]
    public void ApplyCurve_IsContinuous(float boost)
    {
        var bg = 0.15f;
        var sp = bg * (1f + 0.1f * boost);
        var hp = 0.85f;

        // At SP boundary — function is continuous (both sides → sp),
        // but the midtone zone has steep slope (pow(t, <1) has infinite derivative at 0),
        // so we use a very small epsilon.
        var spEps = 0.0001f;
        var belowSp = ApplyCurve(sp - spEps, boost, bg);
        var atSp = ApplyCurve(sp, boost, bg);
        var aboveSp = ApplyCurve(sp + spEps, boost, bg);
        Math.Abs(belowSp - atSp).ShouldBeLessThan(0.01f, $"Discontinuity at SP: below={belowSp:F6}, at={atSp:F6}");
        Math.Abs(aboveSp - atSp).ShouldBeLessThan(0.02f, $"Discontinuity at SP: above={aboveSp:F6}, at={atSp:F6}");

        // At HP boundary
        var hpEps = 0.001f;
        var belowHp = ApplyCurve(hp - hpEps, boost, bg);
        var aboveHp = ApplyCurve(hp + hpEps, boost, bg);
        Math.Abs(belowHp - aboveHp).ShouldBeLessThan(0.01f, $"Discontinuity at HP: below={belowHp:F6}, above={aboveHp:F6}");
    }
}
