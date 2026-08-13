using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;

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
            ILogger? logger = null)
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
            master.WriteToFitsFile(temp, wcs: null, extras);
            File.Move(temp, path, overwrite: true);
            logger?.LogDebug("  [{Session}] session master retained", sessionId);
            return true;
        }

        /// <summary>
        /// Reads a session's retained master.
        /// </summary>
        /// <returns><see langword="false"/> when no master was retained for this session, or the file
        /// cannot be decoded. Both are ordinary: retention is best-effort and a store built before it
        /// existed has none, so a caller treats this as "take the expensive path" rather than an error.
        /// </returns>
        public static bool TryRead(string outDir, string sessionId, out Image master, ILogger? logger = null)
        {
            master = null!;
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
