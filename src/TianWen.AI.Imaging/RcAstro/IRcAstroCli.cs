using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Thin seam over the RC-Astro command-line tool (<c>rc-astro</c>). Locates
    /// the executable, reports per-product license status, and runs a product
    /// against a FITS file while parsing its <c>--json</c> NDJSON event stream.
    /// </summary>
    /// <remarks>
    /// RC-Astro's neural models ship encrypted on disk and are only decryptable
    /// by the official binary, so (unlike the SETI Astro ONNX models) they
    /// cannot be loaded into ONNX Runtime directly. The supported integration
    /// path is the documented machine protocol, which this drives.
    /// </remarks>
    public interface IRcAstroCli
    {
        /// <summary>Absolute path to the located <c>rc-astro</c> executable, or null.</summary>
        string? ExecutablePath { get; }

        /// <summary>True when the executable was located on this machine.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// True when <paramref name="productKey"/> ("bxt" / "sxt" / "nxt") is
        /// licensed on this machine. Probes <c>rc-astro &lt;product&gt; --license</c>
        /// once and caches the result. Synchronous by design so it can gate a
        /// DI factory; returns false when the executable is absent.
        /// </summary>
        bool IsLicensed(string productKey);

        /// <summary>
        /// Runs <c>rc-astro &lt;productKey&gt; &lt;inputPath&gt; -o &lt;outputPath&gt;</c>
        /// with the shared machine-mode flags (<c>--depth 32F --engine ... --overwrite --json</c>)
        /// plus <paramref name="extraArgs"/>, streaming progress out of the
        /// NDJSON event stream. Throws <see cref="RcAstroCliException"/> on a
        /// non-zero exit or an <c>error</c> event.
        /// </summary>
        Task<RcAstroRunResult> RunAsync(
            string productKey,
            string inputPath,
            string outputPath,
            IReadOnlyList<string> extraArgs,
            IProgress<RcAstroProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
