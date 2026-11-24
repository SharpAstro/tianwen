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
    /// by the official binary, so (unlike a plain ONNX model) they
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
        /// and caches a GRANTED licence; a refusal is probed again on the next call.
        /// Synchronous by design so it can gate a DI factory; returns false when the
        /// executable is absent.
        /// </summary>
        bool IsLicensed(string productKey);

        /// <summary>
        /// Why <paramref name="productKey"/> was last refused, in words for a status line ("its licence
        /// check exited 77: ..."), or null when it was not refused or not yet asked.
        /// </summary>
        string? LicenseRefusal(string productKey) => null;

        /// <summary>
        /// The CLI's own version ("2.6.11"), or null where it could not be read: no executable, or a CLI that does not
        /// describe itself in JSON. Asked once a process (<c>rc-astro --json</c>, which reads what this computer has
        /// stored and asks no licence server). It is said beside a result and never part of a step's key: two CLI
        /// versions that run one model gave the same pixels to rounding (2.6.10 and 2.6.11, measured 2026-11-24).
        /// </summary>
        string? CliVersion => null;

        /// <summary>
        /// The model version a run of <paramref name="productKey"/> ("bxt" / "sxt" / "nxt") uses: the newest one
        /// installed ("5", "3.1"), since no run here names one (<c>--ml-version</c> stays at its default, the latest).
        /// Null where it could not be read. A licence line says less: "licensed through ML3" covers ML3.1. A new
        /// model is other pixels, so a role's name carries this (<see cref="RcAstroEnhancerBase.Name"/>) and a
        /// workbench step is keyed by it.
        /// </summary>
        string? ModelVersion(string productKey) => null;

        /// <summary>
        /// Runs <c>rc-astro &lt;productKey&gt; &lt;inputPath&gt; -o &lt;outputPath&gt;</c>
        /// with the shared machine-mode flags (<c>--depth 32F --overwrite --json</c>)
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
