using TianWen.Lib.Devices.Guider;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Mutable configuration snapshot for the built-in guider, used by the equipment tab.
/// <see cref="NeuralBlendPercent"/> is displayed as 0–100% and stored as 0.0–1.0 in the URI.
/// </summary>
public record struct BuiltInGuiderConfig(
    PulseGuideSource PulseGuideSource,
    bool ReverseDecAfterFlip,
    bool UseNeuralGuider,
    int NeuralBlendPercent)
{
    public static readonly BuiltInGuiderConfig Default = new(
        PulseGuideSource.Auto,
        ReverseDecAfterFlip: true,
        UseNeuralGuider: true,
        NeuralBlendPercent: 15);
}
