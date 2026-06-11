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
///   <item><c>reuseCalibration</c> — reuse a saved calibration after a quick pulse validation (default true)</item>
///   <item><c>reverseDecAfterFlip</c> — reverse DEC corrections after a meridian flip (default true)</item>
///   <item><c>useNeuralGuider</c> / <c>neuralBlendFactor</c> — opt-in neural guiding + blend (0-1)</item>
///   <item>Advanced: <c>maxCalibrationAttempts</c>, <c>maxRecalibrationAttempts</c>,
///     <c>calibrationRetryDelaySeconds</c>, <c>neuralSettleFailSafeFraction</c> (0-1)</item>
/// </list>
/// </summary>
public record class BuiltInGuiderDevice(Uri DeviceUri) : GuiderDeviceBase(DeviceUri)
{
    private const string DefaultDeviceId = "builtin";
    private const string DefaultDisplayName = "Built-in Guider";

    // Defaults for the advanced (collapsed-by-default) knobs. Single source of truth shared by
    // the parsing properties below, the Settings descriptors, and BuiltInGuiderDriver.
    internal const int DefaultMaxCalibrationAttempts = 3;
    internal const int DefaultMaxRecalibrationAttempts = 3;
    internal const int DefaultCalibrationRetryDelaySeconds = 5;
    internal const double DefaultNeuralSettleFailSafeFraction = 0.5;

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
        return val is not null && bool.TryParse(val, out var b) && b;
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
            defaultValue: false, trueLabel: "On", falseLabel: "Off"),
        DeviceSettingHelper.PercentSetting(
            NeuralBlendKey, "Neural Blend",
            defaultPercent: 50,
            isVisible: IsNeuralEnabled),
        // Advanced: expert knobs with sensible defaults, rendered under a collapsed sub-section.
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.MaxCalibrationAttempts.Key, "Calib. Attempts",
            defaultValue: DefaultMaxCalibrationAttempts, min: 1, max: 10, step: 1,
            isAdvanced: true),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.MaxRecalibrationAttempts.Key, "Recalib. Attempts",
            defaultValue: DefaultMaxRecalibrationAttempts, min: 1, max: 10, step: 1,
            isAdvanced: true),
        DeviceSettingHelper.IntSetting(
            DeviceQueryKey.CalibrationRetryDelaySeconds.Key, "Calib. Retry Delay",
            defaultValue: DefaultCalibrationRetryDelaySeconds, min: 0, max: 60, step: 5,
            suffix: "s", isAdvanced: true),
        DeviceSettingHelper.PercentSetting(
            DeviceQueryKey.NeuralSettleFailSafeFraction.Key, "Settle Fail-Safe",
            defaultPercent: (int)(DefaultNeuralSettleFailSafeFraction * 100),
            isVisible: IsNeuralEnabled, isAdvanced: true),
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
    /// Defaults to <c>false</c> — neural guiding is opt-in. It is experimental: a model trained
    /// online can become net-harmful and drift an axis (notably Dec) badly before the
    /// performance monitor can disable it, so it must be explicitly turned on. The pure
    /// P-controller is the default and guides sub-arcsec on its own.
    /// </summary>
    internal bool UseNeuralGuider
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.UseNeuralGuider);
            return value is not null && bool.TryParse(value, out var result) && result;
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

    /// <summary>
    /// Maximum calibration attempts before giving up. A failed attempt (cloud, poor seeing, no
    /// usable star) is retried -- transient conditions often clear -- rather than abandoning the
    /// session. Advanced setting; defaults to <see cref="DefaultMaxCalibrationAttempts"/>.
    /// </summary>
    internal int MaxCalibrationAttempts => IntQueryValue(DeviceQueryKey.MaxCalibrationAttempts, DefaultMaxCalibrationAttempts);

    /// <summary>
    /// Maximum number of recalibration attempts after the guide loop reports a divergence it
    /// cannot recover from. Beyond this, guiding gives up with an error rather than recalibrating
    /// forever. Advanced setting; defaults to <see cref="DefaultMaxRecalibrationAttempts"/>.
    /// </summary>
    internal int MaxRecalibrationAttempts => IntQueryValue(DeviceQueryKey.MaxRecalibrationAttempts, DefaultMaxRecalibrationAttempts);

    /// <summary>
    /// Delay between calibration attempts, giving transient conditions (a passing cloud) time to
    /// clear. Advanced setting; defaults to <see cref="DefaultCalibrationRetryDelaySeconds"/> seconds.
    /// </summary>
    internal TimeSpan CalibrationRetryDelay =>
        TimeSpan.FromSeconds(IntQueryValue(DeviceQueryKey.CalibrationRetryDelaySeconds, DefaultCalibrationRetryDelaySeconds));

    /// <summary>
    /// Fraction of the settle timeout after which, if guiding still has not settled and the
    /// neural model is engaged, the model is disabled and the settle window restarts on the pure
    /// P-controller. Advanced setting; defaults to <see cref="DefaultNeuralSettleFailSafeFraction"/>.
    /// </summary>
    internal double NeuralSettleFailSafeFraction
    {
        get
        {
            var value = Query.QueryValue(DeviceQueryKey.NeuralSettleFailSafeFraction);
            return value is not null && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var fraction)
                ? fraction
                : DefaultNeuralSettleFailSafeFraction;
        }
    }

    private int IntQueryValue(DeviceQueryKey key, int defaultValue)
    {
        var value = Query.QueryValue(key);
        return value is not null && int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }
}
