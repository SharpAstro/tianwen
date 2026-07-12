using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    public class DatasetMasterCacheTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mastercache-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static ImageMeta BiasMeta(DateTimeOffset start, string instrument = "TestCam") => new(
            Instrument: instrument,
            ExposureStartTime: start,
            ExposureDuration: TimeSpan.Zero,
            FrameType: FrameType.Bias,
            Telescope: "T",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 135,
            FocusPos: -1,
            Filter: Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: -10f,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN,
            Gain: 100);

        private FrameInfo WriteBias(string biasDir, int index, float level, string instrument = "TestCam")
        {
            Directory.CreateDirectory(biasDir);
            var data = Image.CreateChannelData(1, 8, 8);
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    data[0][y, x] = level + (x + y) % 3; // a little structure so median is meaningful
                }
            }
            var image = new Image(data, BitDepth.Float32, maxValue: 65535, minValue: 0, pedestal: 0,
                imageMeta: BiasMeta(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index), instrument));
            var path = Path.Combine(biasDir, $"bias_{index:D3}.fits");
            image.WriteToFitsFile(path);
            image.Release();
            Image.TryReadFitsFile(path, out var reread).ShouldBeTrue();
            return new FrameInfo(path, reread!.Width, reread.Height, reread.ChannelCount, reread.BitDepth, reread.ImageMeta);
        }

        [Fact]
        public void ComputeFingerprint_IsOrderIndependentButCountSensitive()
        {
            var a = WriteBias(Path.Combine(_dir, "a"), 0, 100);
            var b = WriteBias(Path.Combine(_dir, "a"), 1, 100);
            var c = WriteBias(Path.Combine(_dir, "a"), 2, 100);

            var fp1 = MasterCache.ComputeFingerprint([a, b, c]);
            var fp2 = MasterCache.ComputeFingerprint([c, a, b]);
            var fp3 = MasterCache.ComputeFingerprint([a, b]);

            fp1.ShouldBe(fp2);
            fp1.ShouldNotBe(fp3);
            fp1.Length.ShouldBe(16);
        }

        [Fact]
        public async Task GetOrBuild_BuildsThenCacheHits_AndRebuildsWhenInputSetGrows()
        {
            var ct = TestContext.Current.CancellationToken;
            var biasDir = Path.Combine(_dir, "cal");
            var frames = new List<FrameInfo> { WriteBias(biasDir, 0, 100), WriteBias(biasDir, 1, 102), WriteBias(biasDir, 2, 101) };
            var key = MasterGroupKey.FromFrame(frames[0]);
            var train = CalibrationResolver.CalTrain.ForFrame(frames[0]);
            var mastersDir = Path.Combine(_dir, "masters");

            var built = await new MasterCache(mastersDir).GetOrBuildAsync(key, train, frames, ct);
            built.ShouldNotBeNull();
            var masterPath = Directory.GetFiles(mastersDir, "master_*.fits").ShouldHaveSingleItem();
            var mtimeAfterBuild = File.GetLastWriteTimeUtc(masterPath);

            // A fresh cache instance over the same dir + same inputs must NOT rewrite the file.
            await Task.Delay(20, ct);
            var cached = await new MasterCache(mastersDir).GetOrBuildAsync(key, train, frames, ct);
            cached.ShouldNotBeNull();
            File.GetLastWriteTimeUtc(masterPath).ShouldBe(mtimeAfterBuild);

            // Growing the library (a 4th bias) changes the fingerprint -> rebuild.
            frames.Add(WriteBias(biasDir, 3, 103));
            await Task.Delay(20, ct);
            var rebuilt = await new MasterCache(mastersDir).GetOrBuildAsync(key, train, frames, ct);
            rebuilt.ShouldNotBeNull();
            File.GetLastWriteTimeUtc(masterPath).ShouldBeGreaterThan(mtimeAfterBuild);
        }

        [Fact]
        public async Task GetOrBuild_ReturnsNullForTooFewFrames()
        {
            var ct = TestContext.Current.CancellationToken;
            var frames = new List<FrameInfo> { WriteBias(Path.Combine(_dir, "one"), 0, 100) };
            var master = await new MasterCache(Path.Combine(_dir, "masters")).GetOrBuildAsync(
                MasterGroupKey.FromFrame(frames[0]), CalibrationResolver.CalTrain.ForFrame(frames[0]), frames, ct);
            master.ShouldBeNull();
        }

        [Fact]
        public async Task GetOrBuild_DistinctPerCamera_NoCollisionWhenTwoBodiesShareASensor()
        {
            // Two IMX533 bodies: identical sensor/gain/temp -> the SAME MasterGroupKey (instrument is
            // not part of it), but different INSTRUME. The shared cache must build + serve a SEPARATE
            // master per camera, never cross-serve the first body's cached Task or overwrite its file.
            var ct = TestContext.Current.CancellationToken;
            var mastersDir = Path.Combine(_dir, "masters");
            var camA = new List<FrameInfo>
            {
                WriteBias(Path.Combine(_dir, "a"), 0, 100, "ZWO ASI533MC Pro"),
                WriteBias(Path.Combine(_dir, "a"), 1, 101, "ZWO ASI533MC Pro"),
            };
            var camB = new List<FrameInfo>
            {
                WriteBias(Path.Combine(_dir, "b"), 0, 200, "SVBONY SV605CC"),
                WriteBias(Path.Combine(_dir, "b"), 1, 201, "SVBONY SV605CC"),
            };
            var keyA = MasterGroupKey.FromFrame(camA[0]);
            var keyB = MasterGroupKey.FromFrame(camB[0]);
            keyA.ShouldBe(keyB); // same sensor config -> identical key; only the train differs

            var cache = new MasterCache(mastersDir);
            var a = await cache.GetOrBuildAsync(keyA, CalibrationResolver.CalTrain.ForFrame(camA[0]), camA, ct);
            var b = await cache.GetOrBuildAsync(keyB, CalibrationResolver.CalTrain.ForFrame(camB[0]), camB, ct);

            a.ShouldNotBeNull();
            b.ShouldNotBeNull();
            // Two distinct masters on disk, one per body -- proving the second did not reuse the first.
            var files = Directory.GetFiles(mastersDir, "master_*.fits");
            files.Length.ShouldBe(2);
            files.ShouldContain(f => Path.GetFileName(f).Contains("ZWOASI533MCPro"));
            files.ShouldContain(f => Path.GetFileName(f).Contains("SVBONYSV605CC"));
            // ... and their content differs (A ~100, B ~200), so no cross-serve occurred.
            a.GetChannelSpan(0)[0].ShouldBeLessThan(150f);
            b.GetChannelSpan(0)[0].ShouldBeGreaterThan(150f);
        }
    }
}
