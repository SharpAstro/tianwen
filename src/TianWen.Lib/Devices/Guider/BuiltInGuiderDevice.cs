using System;
using System.Collections.Immutable;
using System.Web;
using TianWen.Lib;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Device record for the built-in guider that uses <see cref="GuideLoop"/> internally.
/// Always available (no external software needed). Configuration is carried in URI query parameters:
/// <list type="bullet">
///   <item><c>pulseGuideSource</c> — <see cref="PulseGuideSource"/> (Auto, Camera, Mount)</item>
/// </list>
/// </summary>
public record class BuiltInGuiderDevice(Uri DeviceUri) : GuiderDeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "builtin";
    private const string DefaultDisplayName = "Built-in Guider";

    public BuiltInGuiderDevice()
        : this(new Uri($"{DeviceType.Guider}://{typeof(BuiltInGuiderDevice).Name}/{DefaultDeviceId}#{DefaultDisplayName}"))
    {
    }

    public override string? ProfileName => null;

    public override GuiderCapabilities Capabilities =>
        GuiderCapabilities.ConfigurablePulseGuideSource |
        GuiderCapabilities.ConfigurableDecFlip |
        GuiderCapabilities.NeuralGuiding;

    private static readonly string NeuralBlendKey = DeviceQueryKey.NeuralBlendFactor.Key;
    private static readonly string UseNeuralKey = DeviceQueryKey.UseNeuralGuider.Key;

    private static bool IsNeuralEnabled(Uri uri)
    {
        var val = HttpUtility.ParseQueryString(uri.Query)[UseNeuralKey];
        return val is null || !bool.TryParse(val, out var b) || b;
    }

    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        DeviceSettingHelper.BoolSetting(
            DeviceQueryKey.ReuseCalibration.Key, "Reuse Calibration",
            defaultValue: true),
        DeviceSettingHelper.EnumSetting(
            DeviceQueryKey.PulseGuideSource.Key, "Pulse Guide",
            PulseGuideSource.Auto),
        DeviceSettingHelper.BoolSetting(
            DeviceQueryKey.ReverseDecAfterFlip.Key, "Rev DEC on Flip",
            defaultValue: true),
        DeviceSettingHelper.BoolSetting(
            UseNeuralKey, "Neural Guider",
            defaultValue: true, trueLabel: "On", falseLabel: "Off"),
        DeviceSettingHelper.PercentSetting(
            NeuralBlendKey, "Neural Blend",
            defaultPercent: 50,
            isVisible: IsNeuralEnabled),
    ];

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Guider => new BuiltInGuiderDriver(this, sp),
        _ => null
    };

    /// <summary>
    /// Parses the <see cref="PulseGuideSource"/> from the device URI query string.
    /// Defaults to <see cref="PulseGuideSource.Auto"/> if not specified or invalid.
    /// </summary>
    internal PulseGuideSource PulseGuideSource
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.PulseGuideSource);
            return value is not null && Enum.TryParse<PulseGuideSource>(value, ignoreCase: true, out var source)
                ? source
                : PulseGuideSource.Auto;
        }
    }

    /// <summary>
    /// Whether to reverse DEC guide corrections after detecting a meridian flip.
    /// Defaults to <c>true</c> — most modern GEM mounts require this.
    /// Some older mounts that internally reverse their DEC motor after a flip may need <c>false</c>.
    /// </summary>
    /// <summary>
    /// Whether to reuse a saved calibration from a previous session (validated with a quick pulse test).
    /// Defaults to <c>true</c> (like PHD2). When false, always recalibrates.
    /// </summary>
    internal bool ReuseCalibration
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.ReuseCalibration);
            return value is null || !bool.TryParse(value, out var result) || result;
        }
    }

    internal bool ReverseDecAfterFlip
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.ReverseDecAfterFlip);
            return value is null || !bool.TryParse(value, out var result) || result;
        }
    }

    /// <summary>
    /// Whether to enable the neural guide model for online learning during guiding.
    /// Defaults to <c>true</c> — the model starts from scratch or loads from a previous session.
    /// </summary>
    internal bool UseNeuralGuider
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.UseNeuralGuider);
            return value is null || !bool.TryParse(value, out var result) || result;
        }
    }

    /// <summary>
    /// Target blend factor for neural model corrections (0 = P-only, 1 = neural-only).
    /// Defaults to 0.5 — 50% neural (matches PHD2's prediction_gain).
    /// The effective blend ramps linearly from 0 to this value over ~2 PE cycles.
    /// </summary>
    internal double NeuralBlendFactor
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.NeuralBlendFactor);
            return value is not null && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var factor)
                ? factor
                : 0.5;
        }
    }
}
