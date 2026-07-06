using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Which display-side renditions <see cref="MasterPostProcessor"/> emits for a stacked
/// master. These are the stretched, display-referred artifacts (never the linear FITS/EXR
/// masters or the <c>--split-plates</c> TIFFs, which are governed separately) -- each is a
/// self-contained quick-look/delivery file derived from the SAME single SPCC + stretch solve.
/// A <see cref="FlagsAttribute"/> set rather than a bool per output so the emit selection is
/// one value that threads cleanly through the post-processor and extends without growing the
/// method signatures.
/// </summary>
[Flags]
public enum MasterRenderOutputs
{
    /// <summary>Emit nothing display-side (linear FITS/EXR only).</summary>
    None = 0,

    /// <summary>16-bit sRGB (or cICP-PQ) PNG preview -- the default quick-look.</summary>
    PreviewPng = 1 << 0,

    /// <summary>Ultra HDR (gain-map / hdrgm 1.0) JPEG, recovering the highlights the SDR
    /// stretch clipped (<see cref="Image.RenderHdrLinearRgb"/>). Its SDR base rendition is
    /// the same stretched raster the PNG carries, so it is independent of
    /// <see cref="PreviewPng"/> -- either, both, or neither may be set.</summary>
    UltraHdr = 1 << 1,
}
