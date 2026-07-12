using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Shared setup for the dataset-builder tests: reads FITS frames into <see cref="FrameInfo"/>
    /// handles and drives a full <see cref="SessionRegistrar"/> pass over the synthetic RGGB fixture
    /// (<see cref="RgbBayerSyntheticFixture"/>) so the tile-export / stats / split tests all start
    /// from one canonical registered session instead of re-implementing the wiring.
    /// </summary>
    internal static class DatasetSyntheticFixtures
    {
        public static List<FrameInfo> ReadFrames(string dir, string pattern)
        {
            var frames = new List<FrameInfo>();
            foreach (var path in Directory.GetFiles(dir, pattern).OrderBy(p => p, StringComparer.Ordinal))
            {
                Image.TryReadFitsFile(path, out var img).ShouldBeTrue();
                frames.Add(new FrameInfo(path, img!.Width, img.Height, img.ChannelCount, img.BitDepth, img.ImageMeta));
                img.Release();
            }
            return frames;
        }

        /// <summary>Writes the synthetic lights + darks under <paramref name="rootDir"/>, builds a
        /// dark-master Calibrator, and registers the session (scratch under <paramref name="rootDir"/>).</summary>
        public static async Task<SessionRegistrar.RegisteredSession> RegisterAsync(
            string rootDir, CancellationToken ct, int minSubs = 4, string relativeDir = "synth/rggb")
        {
            var lightsDir = Path.Combine(rootDir, "LIGHT");
            var darksDir = Path.Combine(rootDir, "DARK");
            Directory.CreateDirectory(lightsDir);
            Directory.CreateDirectory(darksDir);
            RgbBayerSyntheticFixture.WriteSyntheticLights(lightsDir);
            RgbBayerSyntheticFixture.WriteSyntheticDarks(darksDir);

            var calibrator = new Calibrator(Dark: await MasterFrameBuilder.BuildDarkMasterAsync(ReadFrames(darksDir, "dark_*.fits"), ct));
            var session = new ImagingSession(lightsDir, relativeDir, "SynthBayer", "SynthRgb", [.. ReadFrames(lightsDir, "light_*.fits")]);
            var registered = await SessionRegistrar.RegisterAsync(
                session, calibrator, Path.Combine(rootDir, "scratch"), minSubs: minSubs, cancellationToken: ct);
            registered.ShouldNotBeNull();
            return registered;
        }
    }
}
