using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A single panel in a mosaic grid, with its target coordinates and grid position.
/// <see cref="TransitTimeHours"/> equals the panel's RA — panels are ordered by RA ascending
/// so that eastern panels (crossing the meridian first) are imaged first.
/// </summary>
/// <param name="Target">Sky coordinates and name for this panel</param>
/// <param name="Row">Row index in the mosaic grid (0 = southernmost)</param>
/// <param name="Column">Column index in the mosaic grid (0 = easternmost / lowest RA)</param>
/// <param name="TransitTimeHours">RA in hours — LST at transit; used for meridian-aware ordering</param>
public readonly record struct MosaicPanel(
    Target Target,
    int Row,
    int Column,
    double TransitTimeHours
);
