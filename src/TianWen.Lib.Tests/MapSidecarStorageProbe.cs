using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated: what the map sidecars in a real store would cost once stored the way
    /// <see cref="IntegrationFitsWriter.MapStorage"/> stores them.
    ///
    /// <para>This exists because the size argument has to be made on REAL maps. A synthetic plane
    /// compresses however its generator happened to be smooth; a bake's coverage planes carry the
    /// actual dither excursion, the actual rejection voids and the actual drizzle weights, and
    /// those are what decide the ratio. The one measurement that started this: a 3072x3060x3
    /// coverage map of 81 frames is 112.8 MB as float32 and gzips to 94.7 MB, a ratio of 1.2,
    /// because float mantissa noise does not compress. The same map quantised first is 1.71 MB.</para>
    ///
    /// <para>Set <c>TIANWEN_MAP_PROBE</c> to a store folder (walked recursively for sidecars) or to
    /// a single sidecar. Optional <c>TIANWEN_MAP_PROBE_LIMIT</c> (default 20) bounds how many are
    /// re-encoded, since each one is a full decode plus a full compress.</para>
    /// </summary>
    public sealed class MapSidecarStorageProbe(ITestOutputHelper output)
    {
        [Fact]
        public void WhatTheStoresMapsWouldCostQuantised()
        {
            var root = Environment.GetEnvironmentVariable("TIANWEN_MAP_PROBE");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(root), "TIANWEN_MAP_PROBE not set");

            var limit = int.TryParse(
                Environment.GetEnvironmentVariable("TIANWEN_MAP_PROBE_LIMIT"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 20;

            var candidates = Directory.Exists(root)
                ? FileEnumeration.EnumerateFiles(root, ".fits", recursive: true)
                    .Where(IntegrationFitsWriter.IsMapSidecarPath)
                    .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Repeat(root!, 1);
            var maps = candidates.Take(limit).ToArray();

            maps.Length.ShouldBeGreaterThan(0, $"no map sidecars under {root}");

            var scratch = Path.Combine(Path.GetTempPath(), "tianwen-map-probe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                long before = 0;
                long after = 0;
                output.WriteLine("        before |        after |  ratio | depth | bscale     | map");
                foreach (var path in maps)
                {
                    if (!Image.TryReadFitsFile(path, out var map))
                    {
                        output.WriteLine($"  UNREADABLE {Path.GetFileName(path)}");
                        continue;
                    }

                    var storage = IntegrationFitsWriter.MapStorage(map);
                    // Through the real writer, so the number includes the headers and the padding
                    // and is not an estimate of the pixel block alone.
                    var target = Path.Combine(scratch, "probe.fits" + Image.GzipSuffix);
                    map.WriteToFitsFile(target, wcs: null, extraHeaders: null, storage);

                    var originalSize = new FileInfo(path).Length;
                    var storedSize = new FileInfo(target).Length;
                    before += originalSize;
                    after += storedSize;
                    output.WriteLine(
                        $"  {originalSize / 1e6,10:F2} MB | {storedSize / 1e6,10:F3} MB | {originalSize / (double)storedSize,6:F1}x "
                            + $"| {(int)storage.Depth,5} | {storage.BScale,10:G4} | {Path.GetFileName(path)}");
                    File.Delete(target);
                }

                output.WriteLine(
                    $"  TOTAL over {maps.Length} maps: {before / 1e6:F1} MB -> {after / 1e6:F1} MB "
                        + $"({(after > 0 ? before / (double)after : 0):F1}x, {(before - after) / 1e6:F1} MB saved)");
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (IOException)
                {
                    // A locked temp file must not fail the probe.
                }
            }
        }
    }
}
