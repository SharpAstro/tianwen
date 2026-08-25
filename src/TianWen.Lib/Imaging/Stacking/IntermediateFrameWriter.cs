using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Stacking
{
    /// <summary>
    /// Persists the per-frame intermediates a stack normally throws away: the CALIBRATED frame
    /// (bias / dark / flat applied, before registration) and later the NORMALIZED one. Off by
    /// default, opt-in through <c>--save-calibrated</c> / <c>--save-normalized</c>, and written
    /// beside the run under <c>_staging/&lt;slug&gt;/</c>.
    ///
    /// <remarks>
    /// <para>Astro Pixel Processor and PixInsight both keep these, and for the same reason: when a
    /// master comes out wrong, the question is always WHICH STAGE did it, and that is unanswerable
    /// from the master alone. The immediate case here was a star remover behaving oddly on the
    /// comet layer -- deciding whether the fault was the calibration, the mosaic, or the remover
    /// needed the exact pixels the remover was handed, and nothing on disk held them.</para>
    /// <para>These are DIAGNOSTIC outputs, not pipeline inputs. They carry a <c>SRCPATH</c> naming
    /// the frame they came from, and the same <c>SRCDGST</c> digest a starless plate carries, so a
    /// later run can match one back to its origin. They are deliberately NOT re-ingestible as
    /// lights: the scan's provenance skip drops anything with a TianWen <c>SWCREATE</c>, which
    /// these have, so parking them beside the data cannot poison the next stack.</para>
    /// <para>Cost is real and the caller is expected to know it: one float32 frame per light per
    /// enabled kind, so a 135-frame session at 11.6 Mpx is about 6 GB per kind. Nothing prunes
    /// them.</para>
    /// </remarks>
    /// </summary>
    /// <param name="stagingDir">The run's <c>_staging/&lt;slug&gt;</c>; subdirectories are created per kind.</param>
    /// <param name="saveCalibrated">Persist post-calibration, pre-registration frames.</param>
    /// <param name="saveNormalized">Persist post-normalization frames.</param>
    /// <param name="logger">Optional; one line per kind on first write, and a warning on failure.</param>
    public sealed class IntermediateFrameWriter(
        string stagingDir,
        bool saveCalibrated,
        bool saveNormalized,
        ILogger? logger = null)
    {
        private readonly HashSet<string> _announced = [];
        private IReadOnlyList<string> _frameOrder = [];

        /// <summary>
        /// Teach the writer which source path each frame INDEX corresponds to.
        /// <see cref="Integrator"/> is handed a list of <see cref="Image"/> and no paths, so
        /// without this a normalized dump could only be numbered, and a number is not traceable
        /// back to a light. Set by the pipeline from the same ordered match list the strategies
        /// consume, so index i means the same frame on both sides.
        /// </summary>
        public void SetFrameOrder(IReadOnlyList<string> sourcePaths) => _frameOrder = sourcePaths;

        /// <summary>
        /// <see cref="SaveNormalized"/> addressed by position in <see cref="SetFrameOrder"/>.
        /// Falls back to an ordinal name if the order was never set or is short, because a dump
        /// with a weaker name is still worth more than a dropped one.
        /// </summary>
        public void SaveNormalizedByIndex(Image image, int index)
        {
            if (saveNormalized)
            {
                Save("normalized", image, index < _frameOrder.Count ? _frameOrder[index] : $"frame_{index:D4}.fits");
            }
        }

        /// <summary>True when <c>--save-calibrated</c> asked for the calibrated frames. Callers
        /// check this BEFORE materialising anything, so the disabled path costs one bool.</summary>
        public bool WantsCalibrated => saveCalibrated;

        /// <summary>True when <c>--save-normalized</c> asked for the normalized frames.</summary>
        public bool WantsNormalized => saveNormalized;

        /// <summary>True when neither kind is enabled, so a caller can skip constructing one.</summary>
        public bool IsNoOp => !saveCalibrated && !saveNormalized;

        /// <summary>
        /// Write the calibrated frame for <paramref name="sourcePath"/>. A no-op unless
        /// <see cref="WantsCalibrated"/>. Never throws: a diagnostic dump must not be able to kill
        /// a run that was otherwise going to produce a master.
        /// </summary>
        public void SaveCalibrated(Image image, string sourcePath)
        {
            if (saveCalibrated)
            {
                Save("calibrated", image, sourcePath);
            }
        }

        /// <summary>
        /// Write the normalized frame for <paramref name="sourcePath"/>. A no-op unless
        /// <see cref="WantsNormalized"/>. Never throws; see <see cref="SaveCalibrated"/>.
        /// </summary>
        public void SaveNormalized(Image image, string sourcePath)
        {
            if (saveNormalized)
            {
                Save("normalized", image, sourcePath);
            }
        }

        private void Save(string kind, Image image, string sourcePath)
        {
            try
            {
                var dir = Path.Combine(stagingDir, kind);
                Directory.CreateDirectory(dir);
                if (_announced.Add(kind))
                {
                    logger?.LogInformation("  [{Kind}] writing per-frame intermediates to {Dir}", kind, dir);
                }

                var name = Path.GetFileNameWithoutExtension(sourcePath) + "_" + kind + ".fits";
                image.WriteToFitsFile(Path.Combine(dir, name), null, new Dictionary<string, (object Value, string Comment)>
                {
                    [FrameProvenance.SourceDigestKeyword] = (FrameProvenance.SourceDigestOf(sourcePath), "Data digest of the frame this was derived from"),
                    ["SRCPATH"] = (Path.GetFileName(sourcePath), "Frame this intermediate was derived from"),
                    ["TWSTAGE"] = (kind, "Pipeline stage this frame was captured at"),
                    // NOT decoration. IntegrationFitsWriter.IsTianWenProduct keys on this prefix, and
                    // it is the only thing stopping a scan from re-ingesting these as lights: a
                    // calibrated dump inherits IMAGETYP=Light from its source and carries no STACK_N,
                    // so it is invisible to every other provenance check. Dumping 135 of them beside
                    // the originals would otherwise make the next stack of that folder count 270
                    // frames and call it 135.
                    ["SWCREATE"] = (IntegrationFitsWriter.SoftwareCreator, "Software that created this intermediate"),
                });
            }
            catch (Exception ex)
            {
                // Deliberately swallowed. These exist to explain a run, so failing one must not
                // end it -- a full disk mid-session would otherwise lose the master too.
                logger?.LogWarning(ex, "  [{Kind}] could not write intermediate for {Source}", kind, sourcePath);
            }
        }
    }
}
