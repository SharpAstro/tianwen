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
    Processed
}

public static class FrameTypeEx
{
    extension(FrameType frameType)
    {
        public bool NeedsOpenShutter => frameType switch
        {
            FrameType.Light or FrameType.Flat => true,
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