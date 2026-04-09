using System;
using System.Globalization;
using System.Web;

namespace TianWen.Lib.Devices;

/// <summary>
/// Kind of UI control to render for a device setting.
/// </summary>
public enum DeviceSettingKind
{
    BoolToggle,
    EnumCycle,
    IntStepper,
    FloatStepper,
    PercentStepper,
    StringEditor,
}

/// <summary>
/// Describes a single configurable property on a <see cref="DeviceBase"/> subclass.
/// The equipment tab iterates these descriptors to render settings generically.
/// All state is carried in the device URI query parameters.
/// </summary>
/// <param name="Key">URI query parameter key.</param>
/// <param name="Label">Human-readable label shown in the UI.</param>
/// <param name="Kind">UI control type.</param>
/// <param name="FormatValue">Formats the current value from the device URI for display.</param>
/// <param name="Increment">Returns a new URI with the value incremented (for steppers/cycles).</param>
/// <param name="Decrement">Returns a new URI with the value decremented (for steppers). Null for toggles/cycles.</param>
/// <param name="IsVisible">Optional predicate to conditionally hide this row (e.g. blend% only when neural=true).</param>
/// <param name="Placeholder">Placeholder text for <see cref="DeviceSettingKind.StringEditor"/> fields.</param>
/// <param name="Mask">When true, the displayed value is masked (e.g. for API keys). Only used by <see cref="DeviceSettingKind.StringEditor"/>.</param>
public readonly record struct DeviceSettingDescriptor(
    string Key,
    string Label,
    DeviceSettingKind Kind,
    Func<Uri, string> FormatValue,
    Func<Uri, Uri> Increment,
    Func<Uri, Uri>? Decrement = null,
    Func<Uri, bool>? IsVisible = null,
    string? Placeholder = null,
    bool Mask = false);

public static class DeviceSettingHelper
{
    /// <summary>
    /// Returns a new URI with the specified query parameter set to the given value.
    /// </summary>
    public static Uri WithQueryParam(Uri uri, string key, string value)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        query[key] = value;
        var builder = new UriBuilder(uri) { Query = query.ToString() };
        return builder.Uri;
    }

    /// <summary>
    /// Returns a new URI with the specified query parameter removed.
    /// </summary>
    public static Uri WithoutQueryParam(Uri uri, string key)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove(key);
        var builder = new UriBuilder(uri) { Query = query.ToString() };
        return builder.Uri;
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for a bool toggle stored as a query param.
    /// </summary>
    public static DeviceSettingDescriptor BoolSetting(
        string key, string label,
        bool defaultValue = true,
        string trueLabel = "Yes", string falseLabel = "No",
        Func<Uri, bool>? isVisible = null)
    {
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.BoolToggle,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is null || !bool.TryParse(val, out var b) ? defaultValue : b;
                return current ? trueLabel : falseLabel;
            },
            Increment: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is null || !bool.TryParse(val, out var b) ? defaultValue : b;
                return WithQueryParam(uri, key, (!current).ToString());
            },
            IsVisible: isVisible);
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for an enum cycle stored as a query param.
    /// </summary>
    public static DeviceSettingDescriptor EnumSetting<TEnum>(
        string key, string label,
        TEnum defaultValue,
        Func<Uri, bool>? isVisible = null) where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.EnumCycle,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                return val is not null && Enum.TryParse<TEnum>(val, ignoreCase: true, out var e)
                    ? e.ToString()
                    : defaultValue.ToString();
            },
            Increment: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && Enum.TryParse<TEnum>(val, ignoreCase: true, out var e) ? e : defaultValue;
                var idx = Array.IndexOf(values, current);
                var next = values[(idx + 1) % values.Length];
                return WithQueryParam(uri, key, next.ToString());
            },
            IsVisible: isVisible);
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for an integer stepper stored as a query param.
    /// </summary>
    public static DeviceSettingDescriptor IntSetting(
        string key, string label,
        int defaultValue, int min, int max, int step,
        string? suffix = null,
        Func<Uri, bool>? isVisible = null)
    {
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.IntStepper,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && int.TryParse(val, CultureInfo.InvariantCulture, out var i) ? i : defaultValue;
                return suffix is not null ? $"{current}{suffix}" : current.ToString(CultureInfo.InvariantCulture);
            },
            Increment: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && int.TryParse(val, CultureInfo.InvariantCulture, out var i) ? i : defaultValue;
                return WithQueryParam(uri, key, Math.Min(max, current + step).ToString(CultureInfo.InvariantCulture));
            },
            Decrement: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && int.TryParse(val, CultureInfo.InvariantCulture, out var i) ? i : defaultValue;
                return WithQueryParam(uri, key, Math.Max(min, current - step).ToString(CultureInfo.InvariantCulture));
            },
            IsVisible: isVisible);
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for a float stepper stored as a query param.
    /// </summary>
    public static DeviceSettingDescriptor FloatSetting(
        string key, string label,
        double defaultValue, double min, double max, double step,
        string format = "F1", string? suffix = null,
        Func<Uri, bool>? isVisible = null)
    {
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.FloatStepper,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
                return suffix is not null
                    ? $"{current.ToString(format, CultureInfo.InvariantCulture)}{suffix}"
                    : current.ToString(format, CultureInfo.InvariantCulture);
            },
            Increment: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
                return WithQueryParam(uri, key, Math.Min(max, current + step).ToString(CultureInfo.InvariantCulture));
            },
            Decrement: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var current = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
                return WithQueryParam(uri, key, Math.Max(min, current - step).ToString(CultureInfo.InvariantCulture));
            },
            IsVisible: isVisible);
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for a percentage stepper (0–100) stored as a 0.0–1.0 float.
    /// </summary>
    public static DeviceSettingDescriptor PercentSetting(
        string key, string label,
        int defaultPercent, int stepPercent = 5,
        Func<Uri, bool>? isVisible = null)
    {
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.PercentStepper,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var pct = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d)
                    ? (int)Math.Round(d * 100.0)
                    : defaultPercent;
                return $"{pct}%";
            },
            Increment: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var pct = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d)
                    ? (int)Math.Round(d * 100.0)
                    : defaultPercent;
                var newPct = Math.Min(100, pct + stepPercent);
                return WithQueryParam(uri, key, (newPct / 100.0).ToString(CultureInfo.InvariantCulture));
            },
            Decrement: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                var pct = val is not null && double.TryParse(val, CultureInfo.InvariantCulture, out var d)
                    ? (int)Math.Round(d * 100.0)
                    : defaultPercent;
                var newPct = Math.Max(0, pct - stepPercent);
                return WithQueryParam(uri, key, (newPct / 100.0).ToString(CultureInfo.InvariantCulture));
            },
            IsVisible: isVisible);
    }

    /// <summary>
    /// Creates a <see cref="DeviceSettingDescriptor"/> for a free-text string stored as a query param.
    /// The UI renders a text input field; <see cref="DeviceSettingDescriptor.Increment"/> and
    /// <see cref="DeviceSettingDescriptor.Decrement"/> are no-ops (text is set directly by the UI).
    /// </summary>
    /// <param name="placeholder">Placeholder text shown when the value is empty.</param>
    /// <param name="mask">When true, the displayed value is masked (e.g. for API keys).</param>
    public static DeviceSettingDescriptor StringSetting(
        string key, string label,
        string defaultValue = "",
        string? placeholder = null,
        bool mask = false,
        Func<Uri, bool>? isVisible = null)
    {
        return new DeviceSettingDescriptor(
            key, label, DeviceSettingKind.StringEditor,
            FormatValue: uri =>
            {
                var val = HttpUtility.ParseQueryString(uri.Query)[key];
                return val ?? defaultValue;
            },
            // Increment/Decrement are no-ops — the UI sets the value directly via WithQueryParam
            Increment: uri => uri,
            Decrement: uri => uri,
            IsVisible: isVisible,
            Placeholder: placeholder,
            Mask: mask);
    }
}
