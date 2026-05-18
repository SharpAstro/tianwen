using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class MasterFrameBuilderTests
{
    // Deterministic synthetic frame factory. Each frame is a 4x4 mono image
    // filled with `baseline + i * step` per pixel — gives us per-frame
    // distinguishable values we can assert the median against without
    // depending on a seeded RNG. The Bayer-flat tests build similar
    // synthetic patterns where each Bayer quadrant has a known mean.

    private static Image MonoFrame(float[] flat, FrameType type = FrameType.Bias, SensorType sensor = SensorType.Monochrome)
    {
        var channel = new float[4, 4];
        for (var i = 0; i < flat.Length; i++)
        {
            channel[i / 4, i % 4] = flat[i];
        }
        var meta = new ImageMeta(
            Instrument: "synthetic",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(1),
            FrameType: type,
            Telescope: "TestScope",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 400,
            FocusPos: -1,
            Filter: Filter.Luminance,
            BinX: 1,
            BinY: 1,
            CCDTemperature: -10f,
            SensorType: sensor,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN);
        return new Image([channel], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static IReadOnlyList<Image> StackOf(params float[][] flats) =>
        Array.ConvertAll(flats, f => MonoFrame(f));

    [Fact]
    public void BuildBiasMaster_MedianAcrossOddNumberOfFrames_PicksMiddleValue()
    {
        // Five 4x4 frames where pixel (0,0) cycles 0.1, 0.2, 0.3, 0.4, 0.5.
        // Median = 0.3.
        var frames = new List<Image>();
        for (var i = 0; i < 5; i++)
        {
            var pixels = new float[16];
            Array.Fill(pixels, 0f);
            pixels[0] = 0.1f * (i + 1);
            frames.Add(MonoFrame(pixels));
        }

        var master = MasterFrameBuilder.BuildBiasMaster(frames);

        master[0, 0, 0].ShouldBe(0.3f, tolerance: 1e-6f);
        master.ImageMeta.FrameType.ShouldBe(FrameType.Bias);
    }

    [Fact]
    public void BuildBiasMaster_MedianAcrossEvenNumberOfFrames_AveragesMiddleTwo()
    {
        // Four 4x4 frames where pixel (0,0) is 0.1, 0.2, 0.3, 0.4.
        // Sorted middle two = 0.2, 0.3; average = 0.25.
        var frames = new List<Image>();
        for (var i = 0; i < 4; i++)
        {
            var pixels = new float[16];
            pixels[0] = 0.1f * (i + 1);
            frames.Add(MonoFrame(pixels));
        }

        var master = MasterFrameBuilder.BuildBiasMaster(frames);

        master[0, 0, 0].ShouldBe(0.25f, tolerance: 1e-6f);
    }

    [Fact]
    public void BuildBiasMaster_RejectsOutlier_ViaMedian()
    {
        // Five frames where pixel (0,0) is 0.5 in four of them and 99.0
        // in the fifth (cosmic ray hit). Median = 0.5, mean would be ~20.2.
        // This is the whole point of median combine vs mean.
        var frames = new List<Image>();
        for (var i = 0; i < 4; i++)
        {
            var pixels = new float[16];
            pixels[0] = 0.5f;
            frames.Add(MonoFrame(pixels));
        }
        var outlier = new float[16];
        outlier[0] = 99.0f;
        frames.Add(MonoFrame(outlier));

        var master = MasterFrameBuilder.BuildBiasMaster(frames);

        master[0, 0, 0].ShouldBe(0.5f, tolerance: 1e-6f);
    }

    [Fact]
    public void BuildDarkMaster_SameAsBias_StampsFrameTypeDark()
    {
        var frames = StackOf(
            new float[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f },
            new float[] { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f },
            new float[] { 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f, 0.3f });

        var master = MasterFrameBuilder.BuildDarkMaster(frames);

        for (var i = 0; i < 16; i++)
        {
            master[0, i / 4, i % 4].ShouldBe(0.2f, tolerance: 1e-6f);
        }
        master.ImageMeta.FrameType.ShouldBe(FrameType.Dark);
    }

    [Fact]
    public void BuildFlatMaster_NormalizesEachFrameToMean1_BeforeCombine()
    {
        // Two frames with different overall light levels but identical
        // spatial profile (all pixels uniform). After per-frame mean=1
        // normalization, both should match → master is uniform at 1.0.
        var dim = new float[16];
        Array.Fill(dim, 0.2f); // mean = 0.2
        var bright = new float[16];
        Array.Fill(bright, 0.8f); // mean = 0.8

        var frames = new List<Image>
        {
            MonoFrame(dim, type: FrameType.Flat),
            MonoFrame(bright, type: FrameType.Flat),
        };

        var master = MasterFrameBuilder.BuildFlatMaster(frames);

        // Each frame normalized to mean=1.0 individually → both contribute 1.0 per pixel
        for (var i = 0; i < 16; i++)
        {
            master[0, i / 4, i % 4].ShouldBe(1.0f, tolerance: 1e-5f);
        }
        master.ImageMeta.FrameType.ShouldBe(FrameType.Flat);
    }

    [Fact]
    public void BuildFlatMaster_PreservesSpatialVariation_AfterNormalize()
    {
        // Two frames with identical SPATIAL profile (corner brighter than
        // center) but different brightness scale. Normalization should make
        // them identical; median preserves the spatial profile.
        var brighter = new float[]
        {
            2f, 2f, 2f, 2f,
            2f, 1f, 1f, 2f,
            2f, 1f, 1f, 2f,
            2f, 2f, 2f, 2f,
        }; // mean = (12*2 + 4*1)/16 = 28/16 = 1.75
        var dimmer = new float[16];
        for (var i = 0; i < 16; i++) dimmer[i] = brighter[i] * 0.3f;

        var frames = new List<Image>
        {
            MonoFrame(brighter, type: FrameType.Flat),
            MonoFrame(dimmer, type: FrameType.Flat),
        };

        var master = MasterFrameBuilder.BuildFlatMaster(frames);

        // After normalization both inputs have identical values; median = that value.
        // Corner (high) should still be ~2x center (low) after normalize:
        //   brighter normalized: 2/1.75 = 1.143 (corners), 1/1.75 = 0.571 (center)
        master[0, 0, 0].ShouldBe(2f / 1.75f, tolerance: 1e-5f); // corner
        master[0, 1, 1].ShouldBe(1f / 1.75f, tolerance: 1e-5f); // center
    }

    [Fact]
    public void BuildFlatMaster_BayerRGGB_NormalizesPerQuadrantIndependently()
    {
        // 4x4 RGGB Bayer: pixel positions
        //   (0,0)=R (0,1)=G (0,2)=R (0,3)=G
        //   (1,0)=G (1,1)=B (1,2)=G (1,3)=B
        //   (2,0)=R (2,1)=G (2,2)=R (2,3)=G
        //   (3,0)=G (3,1)=B (3,2)=G (3,3)=B
        // Set R-positions to 0.4, G to 0.8, B to 0.2 (typical OSC pattern
        // under white light: green twice as sensitive, blue less so).
        // After Bayer-aware normalization, each Bayer position should be
        // scaled so its position-class mean is 1.0.
        var pixels = new float[16];
        for (var i = 0; i < 16; i++)
        {
            var y = i / 4;
            var x = i % 4;
            var pos = (y % 2) * 2 + (x % 2);
            pixels[i] = pos switch
            {
                0 => 0.4f, // R
                1 or 2 => 0.8f, // G
                _ => 0.2f, // B
            };
        }
        var frame = MonoFrame(pixels, type: FrameType.Flat, sensor: SensorType.RGGB);

        var master = MasterFrameBuilder.BuildFlatMaster(new List<Image> { frame });

        // After per-Bayer-quadrant normalization, every pixel of every
        // position-class is divided by its class mean -> 1.0 everywhere.
        for (var i = 0; i < 16; i++)
        {
            master[0, i / 4, i % 4].ShouldBe(1.0f, tolerance: 1e-5f);
        }
    }

    [Fact]
    public void BuildFlatMaster_BayerRGGB_PreservesIntraQuadrantVariation()
    {
        // Synthetic dust spot: R-positions vary 0.4 (clean) vs 0.2 (dust),
        // G-positions all 0.8, B-positions all 0.2. The Bayer normalization
        // should scale R to its position-class mean (0.3 = mean of 0.4 + 0.2),
        // preserving the relative 2x dust signal at the affected R pixels.
        var pixels = new float[16];
        for (var i = 0; i < 16; i++)
        {
            var y = i / 4;
            var x = i % 4;
            var pos = (y % 2) * 2 + (x % 2);
            pixels[i] = pos switch
            {
                0 => (i == 0 ? 0.2f : 0.4f), // R: one dust pixel, rest clean
                1 or 2 => 0.8f, // G
                _ => 0.2f, // B
            };
        }
        var frame = MonoFrame(pixels, type: FrameType.Flat, sensor: SensorType.RGGB);

        var master = MasterFrameBuilder.BuildFlatMaster(new List<Image> { frame });

        // R-quadrant mean = (0.2 + 0.4 + 0.4 + 0.4) / 4 = 0.35
        // Dust pixel at (0,0): 0.2 / 0.35 ≈ 0.5714
        // Clean R pixel at (0,2): 0.4 / 0.35 ≈ 1.1428
        master[0, 0, 0].ShouldBe(0.2f / 0.35f, tolerance: 1e-4f);
        master[0, 0, 2].ShouldBe(0.4f / 0.35f, tolerance: 1e-4f);
        // G and B positions normalized to 1.0 within their classes.
        master[0, 0, 1].ShouldBe(1.0f, tolerance: 1e-5f); // G
        master[0, 1, 1].ShouldBe(1.0f, tolerance: 1e-5f); // B
    }

    [Fact]
    public void BuildMaster_ShapeMismatch_Throws()
    {
        var ok = MonoFrame(new float[16]);
        var wrongChannel = new float[4, 4];
        var wrongShape = new Image([wrongChannel, wrongChannel], BitDepth.Float32, 1f, 0f, 0f, default);

        Should.Throw<ArgumentException>(() =>
            MasterFrameBuilder.BuildBiasMaster(new List<Image> { ok, wrongShape }));
    }

    [Fact]
    public void BuildMaster_EmptyList_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            MasterFrameBuilder.BuildBiasMaster(Array.Empty<Image>()));
    }

    // ---------- MasterGroupKey ----------

    [Fact]
    public void MasterGroupKey_FromFrame_PopulatesAllFields()
    {
        var meta = new ImageMeta(
            Instrument: "test",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(300),
            FrameType: FrameType.Dark,
            Telescope: "scope",
            PixelSizeX: 3.76f, PixelSizeY: 3.76f, FocalLength: 400, FocusPos: -1,
            Filter: Filter.HydrogenAlpha,
            BinX: 1, BinY: 1,
            CCDTemperature: -10.3f,
            SensorType: SensorType.RGGB, BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN, Longitude: float.NaN,
            Gain: 100, Offset: 50);
        var frame = new FrameInfo("test.fits", 3008, 3008, 1, BitDepth.Float32, meta);

        var key = MasterGroupKey.FromFrame(frame);

        key.Type.ShouldBe(FrameType.Dark);
        key.Exposure.ShouldBe(TimeSpan.FromSeconds(300));
        key.TemperatureC.ShouldBe(-10); // Math.Round(-10.3)
        key.FilterName.ShouldBe(Filter.HydrogenAlpha.Name);
        key.Width.ShouldBe(3008);
        key.Height.ShouldBe(3008);
        key.ChannelCount.ShouldBe(1);
        key.SensorType.ShouldBe(SensorType.RGGB);
        key.Gain.ShouldBe<short>(100);
        key.Offset.ShouldBe(50);
    }

    [Fact]
    public void MasterGroupKey_NaNTemperature_BecomesNull()
    {
        var meta = new ImageMeta(
            "test", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Bias,
            "scope", 3.76f, 3.76f, 400, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown,
            float.NaN, float.NaN);
        var frame = new FrameInfo("test.fits", 100, 100, 1, BitDepth.Float32, meta);

        var key = MasterGroupKey.FromFrame(frame);

        key.TemperatureC.ShouldBeNull();
    }

    [Fact]
    public void MasterGroupKey_EquatesByValue_NotReference()
    {
        var meta = new ImageMeta(
            "test", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), FrameType.Dark,
            "scope", 3.76f, 3.76f, 400, -1, Filter.Luminance, 1, 1,
            -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown,
            float.NaN, float.NaN);
        var frameA = new FrameInfo("a.fits", 100, 100, 1, BitDepth.Float32, meta);
        var frameB = new FrameInfo("b.fits", 100, 100, 1, BitDepth.Float32, meta);

        var keyA = MasterGroupKey.FromFrame(frameA);
        var keyB = MasterGroupKey.FromFrame(frameB);

        // Same metadata, different paths -> same key.
        keyA.ShouldBe(keyB);
        keyA.GetHashCode().ShouldBe(keyB.GetHashCode());
    }

    [Fact]
    public void MasterGroupKey_Slug_BiasSkipsExposure_FlatIncludesFilter()
    {
        var biasKey = new MasterGroupKey(FrameType.Bias, TimeSpan.FromMilliseconds(1), -10, "L", Bandpass.Luminance, 100, 100, 1, SensorType.Monochrome, -1, -1);
        var darkKey = new MasterGroupKey(FrameType.Dark, TimeSpan.FromSeconds(300), -10, "L", Bandpass.Luminance, 100, 100, 1, SensorType.Monochrome, -1, -1);
        var flatKey = new MasterGroupKey(FrameType.Flat, TimeSpan.FromMilliseconds(500), -10, "HydrogenAlpha", Bandpass.Ha, 100, 100, 1, SensorType.RGGB, -1, -1);

        biasKey.Slug().ShouldBe("bias_-10C");
        darkKey.Slug().ShouldBe("dark_300s_-10C");
        flatKey.Slug().ShouldBe("flat_0.5s_-10C_HydrogenAlpha");
    }
}
