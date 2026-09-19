using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.IO;

namespace TianWen.AI.Imaging
{
    /// <summary>
    /// The integrated master kept per session under the output directory, and the only reason a
    /// measurement change does not cost a full re-registration of the archive.
    ///
    /// <para>Scratch is wiped per session, so without retention a master exists nowhere once its
    /// session is done, and re-deriving anything measured on it meant re-reading every sub. That
    /// happened twice in two days for a detection fix and an FWHM fix, neither of which needed the
    /// subs at all.</para>
    ///
    /// <para><b>This type owns the naming.</b> The writer and the reader must agree on the path, and a
    /// reader that recomputed <c>Sanitize(id) + ".fits"</c> for itself is one rename away from silently
    /// finding nothing and falling back to the expensive path, which looks like a slow run rather than
    /// a bug.</para>
    ///
    /// <para><b>What a retained master is NOT good for:</b> changing the integrator. It is the master
    /// that WAS produced, so re-measuring from it is correct for "same master, better measurement code"
    /// and wrong for "re-integrate this session with drizzle instead of AHD". The latter has to
    /// re-register, and the stored <c>MasterStrategy</c> is what keeps the distinction visible.</para>
    /// </summary>
    public static class RetainedMasterStore
    {
        /// <summary>Subdirectory of the dataset output root holding one FITS per session.</summary>
        public const string DirectoryName = "session-masters";

        /// <summary>Suffix used while a master is being written, so a kill mid-write cannot leave a
        /// truncated FITS that a later run mistakes for a complete one.</summary>
        public const string PartialSuffix = ".partial";

        /// <summary>Absolute path a session's retained master lives at.</summary>
        public static string PathFor(string outDir, string sessionId)
            => Path.Combine(outDir, DirectoryName, DatasetTileExporter.Sanitize(sessionId) + ".fits");

        /// <summary>Whether a retained master is present for this session.</summary>
        public static bool Exists(string outDir, string sessionId) => File.Exists(PathFor(outDir, sessionId));

        /// <summary>
        /// Every retained master in the store, in a stable order, with the coverage / rejection
        /// sidecars that sit beside them excluded.
        /// <para>
        /// This exists because the folder holds TWO kinds of <c>.fits</c> since a master started
        /// carrying its coverage plane, and the difference is only in the name
        /// (<see cref="IntegrationFitsWriter.RejectionMapSuffix"/>). Every consumer that addresses a
        /// master by session id through <see cref="PathFor"/> is immune; a caller that walks the
        /// directory is not, and would report twice as many masters as the bake made.
        /// </para>
        /// </summary>
        public static IEnumerable<string> EnumerateMasters(string outDir)
        {
            var dir = Path.Combine(outDir, DirectoryName);
            if (!Directory.Exists(dir))
            {
                return [];
            }

            // Top level only: the store is flat, and a walk that descends would follow a junction
            // into whatever someone parked under the output root.
            return FileEnumeration.EnumerateFiles(dir, ".fits", recursive: false)
                .Where(static p => !IntegrationFitsWriter.IsRejectionMapPath(p))
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Writes the master, unless one is already there. Skipping an existing file is what makes a
        /// resume free, and it also means a stale master survives a re-integration: a caller changing
        /// the integrator must delete the file, which is recorded in the type remarks above.
        /// </summary>
        /// <returns><see langword="true"/> if a file was written, <see langword="false"/> if one was
        /// already present.</returns>
        public static bool Write(
            string outDir,
            string sessionId,
            Image master,
            int frameCount = 0,
            IntegrationStrategyKind? strategy = null,
            ILogger? logger = null,
            WCS? wcs = null,
            Image? rejectionMap = null,
            bool rejectionMapIsCoverage = false,
            double meanRejectionRate = 0.0,
            Image? coverage = null)
        {
            var path = PathFor(outDir, sessionId);
            if (File.Exists(path))
            {
                logger?.LogDebug("  [{Session}] session master already retained", sessionId);
                return false;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // DECLARE OURSELVES. This used to be a bare WriteToFitsFile, which left the master
            // carrying whatever the source subs said: SWCREATE = "N.I.N.A. ..." inherited from the
            // lights, IMAGETYP = Light, and no STACK_N. By TianWen's own provenance rule
            // (IntegrationFitsWriter.IsTianWenProduct: STACK_N > 0 OR a TianWen SWCREATE) a retained
            // master was therefore indistinguishable from a raw light, which is exactly the case the
            // scanner's re-ingestion skip exists to prevent. It was latent only because
            // session-masters/ sits under the dataset output rather than under an archive root, and
            // "latent because of where the file happens to live" is not a property worth relying on.
            //
            // Same two cards the integrator stamps, so one rule recognises both, plus the strategy
            // because a retained master is specifically NOT reusable across a change of integrator.
            var extras = new Dictionary<string, (object Value, string Comment)>
            {
                ["SWCREATE"] = (IntegrationFitsWriter.SoftwareCreator, "Software that created this master"),
            };
            if (frameCount > 0)
            {
                extras["STACK_N"] = (frameCount, "Number of frames combined into this master");
            }
            if (strategy is { } s)
            {
                extras["STRATEGY"] = (s.ToString(), "Integration strategy used (IntegrationStrategyKind)");
            }

            // Write-then-move, so an interrupted write cannot be read back as complete.
            var temp = path + PartialSuffix;
            master.WriteToFitsFile(temp, wcs, extras);
            File.Move(temp, path, overwrite: true);

            // The coverage plane goes beside it, under the stacker's own name and cards, because
            // otherwise every consumer of a bake master is pushed onto CoverageEdgeWalk. The walk
            // ESTIMATES the border from where the noise settles, and partial coverage is a LEVEL about
            // 0.1 percent deep, so it can refuse an edge and keep a ramp that a background model then
            // fits: on the QHY294C SMC master that ramp renders as a 7-level stripe after flattening.
            // Best-effort, and deliberately after the master's own move -- a master without its sidecar
            // is the state every bake before this one produced, and is merely worse, not broken.
            if (rejectionMap is not null)
            {
                try
                {
                    IntegrationFitsWriter.WriteRejectionMap(
                        path, rejectionMap, frameCount, meanRejectionRate, rejectionMapIsCoverage, strategy);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "  [{Session}] could not retain the coverage map; the master stands", sessionId);
                }
            }

            // A staged master's first sidecar is a rejection fraction, which the exact crop tier cannot
            // use; its coverage COUNT goes beside it under its own suffix. Drizzle masters pass null
            // here, since their rejection sidecar already is the coverage.
            if (coverage is not null && !rejectionMapIsCoverage)
            {
                try
                {
                    IntegrationFitsWriter.WriteCoverageMap(path, coverage, frameCount, strategy);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "  [{Session}] could not retain the coverage count; the master stands", sessionId);
                }
            }

            logger?.LogDebug("  [{Session}] session master retained{Wcs}{Map}", sessionId,
                wcs is not null ? " with a WCS" : "",
                rejectionMapIsCoverage || coverage is not null ? " and its coverage map" : rejectionMap is not null ? " and its rejection map" : "");
            return true;
        }

        /// <summary>
        /// Reads a session's retained master.
        /// </summary>
        /// <returns><see langword="false"/> when no master was retained for this session, or the file
        /// cannot be decoded. Both are ordinary: retention is best-effort and a store built before it
        /// existed has none, so a caller treats this as "take the expensive path" rather than an error.
        /// </returns>
        public static bool TryRead(string outDir, string sessionId, [MaybeNullWhen(false)] out Image master, ILogger? logger = null)
        {
            master = null;
            var path = PathFor(outDir, sessionId);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                if (Image.TryReadFitsFile(path, out var image))
                {
                    master = image;
                    return true;
                }

                logger?.LogWarning("  [{Session}] retained master at {Path} could not be decoded; falling back to re-registration", sessionId, path);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "  [{Session}] retained master at {Path} could not be read; falling back to re-registration", sessionId, path);
                return false;
            }
        }
    }
}
