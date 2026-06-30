using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Shared base for the RC-Astro CLI-backed <see cref="IImageEnhancer"/>
    /// implementations. Handles the FITS round-trip (write the input plate -&gt;
    /// invoke the product -&gt; read the output plate) and temp-file lifecycle;
    /// concrete subclasses only declare the product key and its CLI flags.
    /// </summary>
    /// <remarks>
    /// All tiling, normalisation, PSF estimation and demosaicing happen inside
    /// the rc-astro binary, so these wrappers carry none of that logic (unlike
    /// the ONNX enhancers). Pipeline plates are linear Float32 in [0, 1];
    /// <see cref="Image.WriteToFitsFile(string, Astrometry.WCS?)"/> emits
    /// BITPIX=-32 verbatim and RC-Astro normalises internally and returns
    /// [0, 1] 32F, so no rescaling is needed on read-back.
    /// </remarks>
    public abstract class RcAstroEnhancerBase(IRcAstroCli cli, ILogger? logger)
    {
        /// <inheritdoc cref="IImageEnhancer.Name"/>
        public abstract string Name { get; }

        /// <summary>RC-Astro product subcommand key: "bxt" / "sxt" / "nxt".</summary>
        protected abstract string ProductKey { get; }

        /// <summary>
        /// Product-specific CLI flags appended after the shared
        /// input/-o/--depth/--engine/--overwrite/--json arguments. Return an
        /// empty list to run the product on its defaults. <paramref name="tuning"/>
        /// carries optional per-product strength overrides (null fields = the
        /// enhancer's own defaults, i.e. today's behaviour).
        /// </summary>
        protected abstract IReadOnlyList<string> BuildArgs(Image input, EnhanceTuning? tuning);

        /// <inheritdoc cref="IImageEnhancer.EnhanceAsync(Image, CancellationToken)"/>
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => EnhanceAsync(input, EnhanceOptions.Default, null, cancellationToken);

        /// <inheritdoc cref="IImageEnhancer.EnhanceAsync(Image, EnhanceOptions, IProgress{float}, CancellationToken)"/>
        public async Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? stepProgress = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);

            var workDir = Path.Combine(Path.GetTempPath(), "TianWen", "RcAstro");
            Directory.CreateDirectory(workDir);
            var stem = Guid.NewGuid().ToString("N");
            var inputPath = Path.Combine(workDir, $"{stem}-in.fits");
            var outputPath = Path.Combine(workDir, $"{stem}-out.fits");

            try
            {
                input.WriteToFitsFile(inputPath);

                var progress = new Progress<RcAstroProgress>(p =>
                {
                    logger?.LogDebug("RC-Astro {Product}: {Done:F1}% ({Mp:F1} MP/s, eta {Eta:F0}s)",
                        ProductKey, p.PercentDone, p.MegapixelsPerSecond, p.EtaSeconds);
                    stepProgress?.Report((float)(p.PercentDone / 100.0));
                });

                var sw = Stopwatch.StartNew();
                var result = await cli.RunAsync(ProductKey, inputPath, outputPath, BuildArgs(input, options.Tuning), progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!Image.TryReadFitsFile(outputPath, out var enhanced))
                {
                    throw new RcAstroCliException(
                        $"RC-Astro '{ProductKey}' did not produce a readable FITS output at {outputPath}.");
                }

                var (channels, width, height) = enhanced.Shape;
                logger?.LogInformation("RC-Astro {Product} completed on {Device} in {Ms}ms ({W}x{H}x{C})",
                    ProductKey, result.Device ?? "?", sw.ElapsedMilliseconds, width, height, channels);

                return enhanced;
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(outputPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort temp cleanup; a leftover temp file is harmless and
                // must never mask the real result or exception.
            }
        }
    }
}
