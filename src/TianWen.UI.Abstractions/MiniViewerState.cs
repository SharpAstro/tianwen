using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Lightweight state for the mini viewer widget — zoom, pan, stretch.
/// </summary>
public sealed class MiniViewerState
{
    /// <summary>Which camera index to display (0-based). -1 = auto (first available).</summary>
    public int SelectedCameraIndex { get; set; } = -1;

    public bool ZoomToFit { get; set; } = true;
    public float Zoom { get; set; } = 1.0f;
    public (float X, float Y) PanOffset { get; set; }

    public StretchMode StretchMode { get; set; } = StretchMode.Unlinked;
    public int StretchPresetIndex { get; set; }
    public StretchParameters StretchParameters { get; set; } = StretchParameters.Default;

    public float CurvesBoost { get; set; }
    public int CurvesBoostIndex { get; set; }
    public static readonly float[] CurvesBoostPresets = [0f, 0.25f, 0.50f, 1.0f, 1.5f];

    public void CycleStretch()
    {
        StretchMode = StretchMode switch
        {
            StretchMode.None => StretchMode.Unlinked,
            StretchMode.Unlinked => StretchMode.Linked,
            StretchMode.Linked => StretchMode.Luma,
            StretchMode.Luma => StretchMode.None,
            _ => StretchMode.Unlinked
        };
    }

    public void CycleBoost()
    {
        CurvesBoostIndex = (CurvesBoostIndex + 1) % CurvesBoostPresets.Length;
        CurvesBoost = CurvesBoostPresets[CurvesBoostIndex];
    }

    public void CycleStretchPreset()
    {
        StretchPresetIndex = (StretchPresetIndex + 1) % StretchParameters.Presets.Length;
        StretchParameters = StretchParameters.Presets[StretchPresetIndex];
    }
}
