using System;

namespace TianWen.Lib.Imaging;

public enum FrameType
{
    None,
    Light,
    Dark,
    Bias,
    Flat,
    DarkFlat
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
            return Enum.TryParse(v, true, out FrameType frameType) ? frameType : null;
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