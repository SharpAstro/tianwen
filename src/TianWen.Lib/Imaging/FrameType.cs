using System;

namespace TianWen.Lib.Imaging;

public enum FrameType
{
    None,
    Light,
    Dark,
    Bias,
    Flat,
    DarkFlat,

    /// <summary>Not a captured frame at all: an integration, a channel composite, or anything else
    /// a processing package emitted. Astro Pixel Processor writes <c>FRAME = 'Other/Processed'</c>
    /// for these.
    ///
    /// <para>It earns a member rather than mapping to <see cref="None"/> because the two mean
    /// different things to a scan: None is "we could not tell", which is a reason to look harder,
    /// while Processed is a positive statement that the file is DERIVED and must never be ingested
    /// as a light. An APP HOO composite carries no IMAGETYP, no count card and EXPTIME=0, so this
    /// is the only card that says what it is.</para></summary>
    Processed,

    /// <summary>A rung of an auto-focus V-curve, or its verification exposure: a real sky frame
    /// through an open shutter, deliberately taken at a focuser position that is usually WRONG.
    ///
    /// <para>It is a captured frame, so <see cref="Processed"/> would be a lie, and it is emphatically
    /// not a <see cref="Light"/>: an outer rung of the default ladder sits ~100 steps off best focus,
    /// more than ten times the critical focus zone, and integrating one would quietly soften a master.
    /// A distinct member is what makes that structural -- the stacker and the dataset builder both
    /// select <see cref="Light"/> and treat everything else as neither light nor calibration, so these
    /// frames are excluded by the same mechanism that excludes darks, rather than by a path convention
    /// or a provenance heuristic that a later refactor could weaken.</para>
    ///
    /// <para>Appended rather than inserted: the JSON contracts serialise enums numerically, so the
    /// existing members' values are load-bearing.</para></summary>
    Focus,

    /// <summary>A scout / probe exposure: a short frame of the target taken to COUNT STARS, not to
    /// integrate -- the FOV-obstruction probe and the nudge test.
    ///
    /// <para>Unlike <see cref="Focus"/> this one is in focus and points where the lights point, which
    /// is exactly why it needs saying: it differs from a light only in exposure, so nothing about the
    /// pixels would stop a scan ingesting it, and it would form its own plausible-looking
    /// short-exposure group in the stacker rather than being obviously wrong.</para></summary>
    Scout
}

public static class FrameTypeEx
{
    extension(FrameType frameType)
    {
        public bool NeedsOpenShutter => frameType switch
        {
            FrameType.Light or FrameType.Flat or FrameType.Focus or FrameType.Scout => true,
            _ => false
        };

        public string ToFITSValue() => frameType.ToString();
    }

    extension(FrameType)
    {
        /// <summary>Parses a FITS IMAGETYP / FRAMETYP value into a <see cref="FrameType"/>. Strips a
        /// leading "MASTER" so an already-integrated master's IMAGETYP (N.I.N.A.'s "MASTERDARK" /
        /// "MASTERFLAT" / "MASTERBIAS", or "MASTERDARKFLAT") resolves to its underlying frame type;
        /// callers that need to know whether it is a master (vs a raw sub) check
        /// <see cref="IsMasterFITSValue"/> separately (surfaced on <see cref="ImageMeta.IsMaster"/>).
        /// Also accepts the SBFITSEXT spellings MaxIm DL / TheSkyX write ("Light Frame",
        /// "Bias Frame", "Dark Frame", "Flat Field"): a trailing "Frame" is noise and "Flat Field"
        /// IS the flat -- without these, an entire MaxIm-authored archive read as
        /// <see cref="FrameType.None"/> and was invisible to session discovery and the stacker.
        /// Returns null for anything that is not a recognised frame type (e.g. "BADPIXELMAP"), which
        /// both the dataset builder and the stacker treat as neither light nor calibration -&gt; excluded.</summary>
        public static FrameType? FromFITSValue(string value)
        {
            if (value is null)
            {
                return null;
            }
            var v = value.Replace("-", "").Replace(" ", "");
            if (v.StartsWith("MASTER", StringComparison.OrdinalIgnoreCase))
            {
                v = v[6..];
            }
            if (v.EndsWith("FRAME", StringComparison.OrdinalIgnoreCase))
            {
                v = v[..^5];
            }
            if (v.Equals("FLATFIELD", StringComparison.OrdinalIgnoreCase))
            {
                v = "FLAT";
            }
            // Astro Pixel Processor's FRAME card: 'Other/Processed' for a derived product, and the
            // bare 'Other' that older versions emit. The slash survives the normalisation above, so
            // match on the whole token rather than trying to split it.
            if (v.Equals("Other/Processed", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Other", StringComparison.OrdinalIgnoreCase)
                || v.Equals("Processed", StringComparison.OrdinalIgnoreCase))
            {
                v = nameof(FrameType.Processed);
            }
            return v.Length > 0 && Enum.TryParse(v, true, out FrameType frameType) ? frameType : null;
        }

        /// <summary>True when a FITS IMAGETYP / FRAMETYP value denotes a MASTER calibration frame (an
        /// already-integrated dark / flat / bias, e.g. N.I.N.A.'s "MASTERDARK" or Astro Pixel
        /// Processor's master output) rather than a raw sub. Surfaced on
        /// <see cref="ImageMeta.IsMaster"/> so the dataset builder can ingest a foreign master
        /// directly (no &gt;=2-raw rebuild) while the stacker skips masters to stay raw-only.</summary>
        public static bool IsMasterFITSValue(string value) =>
            value is not null
            && value.Replace("-", "").Replace(" ", "").StartsWith("MASTER", StringComparison.OrdinalIgnoreCase);
    }
}