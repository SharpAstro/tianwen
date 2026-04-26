using System;
using System.Collections.Immutable;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The kind of UI control to render for a config field.
    /// </summary>
    public enum ConfigFieldKind
    {
        /// <summary>Integer stepper with [-]/[+] buttons.</summary>
        IntStepper,
        /// <summary>Floating-point stepper with [-]/[+] buttons.</summary>
        FloatStepper,
        /// <summary>TimeSpan stepper (displayed as minutes or seconds).</summary>
        TimeSpanStepper,
        /// <summary>Nullable TimeSpan stepper (None / value).</summary>
        NullableTimeSpanStepper,
        /// <summary>Boolean toggle (ON/OFF).</summary>
        BoolToggle,
        /// <summary>Enum cycle button.</summary>
        EnumCycle,
    }

    /// <summary>
    /// Describes a single field in the session configuration form.
    /// </summary>
    public sealed class ConfigFieldDescriptor(
        string label,
        ConfigFieldKind kind,
        string unit,
        Func<SessionConfiguration, string> formatValue,
        Func<SessionConfiguration, SessionConfiguration> increment,
        Func<SessionConfiguration, SessionConfiguration> decrement)
    {
        public string Label { get; } = label;
        public ConfigFieldKind Kind { get; } = kind;
        public string Unit { get; } = unit;
        public Func<SessionConfiguration, string> FormatValue { get; } = formatValue;
        public Func<SessionConfiguration, SessionConfiguration> Increment { get; } = increment;
        public Func<SessionConfiguration, SessionConfiguration> Decrement { get; } = decrement;
    }

    /// <summary>
    /// A named group of configuration fields.
    /// </summary>
    public sealed class ConfigGroup(string name, ImmutableArray<ConfigFieldDescriptor> fields)
    {
        public string Name { get; } = name;
        public ImmutableArray<ConfigFieldDescriptor> Fields { get; } = fields;
    }

    /// <summary>
    /// Renderer-agnostic grouping of <see cref="SessionConfiguration"/> fields.
    /// Both GUI and TUI iterate these groups to build their respective UIs.
    /// </summary>
    public static class SessionConfigGroups
    {
        public static ImmutableArray<ConfigGroup> Groups { get; } = BuildGroups();

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
            {
                return $"{ts.TotalHours:0.#}h";
            }
            if (ts.TotalMinutes >= 1)
            {
                return $"{ts.TotalMinutes:0}min";
            }
            return $"{ts.TotalSeconds:0}s";
        }

        private static string FormatNullableTimeSpan(TimeSpan? ts)
        {
            return ts.HasValue ? FormatTimeSpan(ts.Value) : "None";
        }

        private static TimeSpan StepTimeSpan(TimeSpan ts, TimeSpan step, bool up)
        {
            var result = up ? ts + step : ts - step;
            return result < TimeSpan.Zero ? TimeSpan.Zero : result;
        }

        private static TimeSpan ClampMin(TimeSpan ts, TimeSpan min)
            => ts < min ? min : ts;

        private static TimeSpan? StepNullableTimeSpan(TimeSpan? ts, TimeSpan step, TimeSpan initial, bool up)
        {
            if (!ts.HasValue)
            {
                return up ? initial : null;
            }

            if (!up && ts.Value <= step)
            {
                return null;
            }

            var result = up ? ts.Value + step : ts.Value - step;
            return result < TimeSpan.Zero ? null : result;
        }

        private static ImmutableArray<ConfigGroup> BuildGroups()
        {
            return
            [
                // CCD setpoint temp is per-camera (shown in Camera Settings panel, not here)
                new ConfigGroup("Cooling Ramps",
                [
                    new ConfigFieldDescriptor(
                        "Cooldown Ramp", ConfigFieldKind.TimeSpanStepper, "",
                        c => FormatTimeSpan(c.CooldownRampInterval),
                        c => c with { CooldownRampInterval = StepTimeSpan(c.CooldownRampInterval, TimeSpan.FromSeconds(30), true) },
                        c => c with { CooldownRampInterval = ClampMin(StepTimeSpan(c.CooldownRampInterval, TimeSpan.FromSeconds(30), false), TimeSpan.FromSeconds(20)) }),
                    new ConfigFieldDescriptor(
                        "Warmup Ramp", ConfigFieldKind.TimeSpanStepper, "",
                        c => FormatTimeSpan(c.WarmupRampInterval),
                        c => c with { WarmupRampInterval = StepTimeSpan(c.WarmupRampInterval, TimeSpan.FromSeconds(30), true) },
                        c => c with { WarmupRampInterval = ClampMin(StepTimeSpan(c.WarmupRampInterval, TimeSpan.FromSeconds(30), false), TimeSpan.FromSeconds(20)) }),
                ]),

                new ConfigGroup("Guiding",
                [
                    new ConfigFieldDescriptor(
                        "Dither Pixels", ConfigFieldKind.FloatStepper, "px",
                        c => $"{c.DitherPixel:0.0}",
                        c => c with { DitherPixel = Math.Min(c.DitherPixel + 0.5, 50.0) },
                        c => c with { DitherPixel = Math.Max(c.DitherPixel - 0.5, 0.0) }),
                    new ConfigFieldDescriptor(
                        "Settle Pixels", ConfigFieldKind.FloatStepper, "px",
                        c => $"{c.SettlePixel:0.0}",
                        c => c with { SettlePixel = Math.Min(c.SettlePixel + 0.1, 10.0) },
                        c => c with { SettlePixel = Math.Max(c.SettlePixel - 0.1, 0.1) }),
                    new ConfigFieldDescriptor(
                        "Dither Every Nth", ConfigFieldKind.IntStepper, "",
                        c => $"{c.DitherEveryNthFrame}",
                        c => c with { DitherEveryNthFrame = Math.Min(c.DitherEveryNthFrame + 1, 20) },
                        c => c with { DitherEveryNthFrame = Math.Max(c.DitherEveryNthFrame - 1, 1) }),
                    new ConfigFieldDescriptor(
                        "Settle Time", ConfigFieldKind.TimeSpanStepper, "",
                        c => FormatTimeSpan(c.SettleTime),
                        c => c with { SettleTime = StepTimeSpan(c.SettleTime, TimeSpan.FromSeconds(5), true) },
                        c => c with { SettleTime = StepTimeSpan(c.SettleTime, TimeSpan.FromSeconds(5), false) }),
                    new ConfigFieldDescriptor(
                        "Guiding Tries", ConfigFieldKind.IntStepper, "",
                        c => $"{c.GuidingTries}",
                        c => c with { GuidingTries = Math.Min(c.GuidingTries + 1, 10) },
                        c => c with { GuidingTries = Math.Max(c.GuidingTries - 1, 1) }),
                ]),

                new ConfigGroup("Horizon",
                [
                    new ConfigFieldDescriptor(
                        "Min Altitude", ConfigFieldKind.IntStepper, "°",
                        c => $"{c.MinHeightAboveHorizon}",
                        c => c with { MinHeightAboveHorizon = (byte)Math.Min(c.MinHeightAboveHorizon + 5, 60) },
                        c => c with { MinHeightAboveHorizon = (byte)Math.Max(c.MinHeightAboveHorizon - 5, 5) }),
                    new ConfigFieldDescriptor(
                        "Max Wait Rising", ConfigFieldKind.NullableTimeSpanStepper, "",
                        c => FormatNullableTimeSpan(c.MaxWaitForRisingTarget),
                        c => c with { MaxWaitForRisingTarget = StepNullableTimeSpan(c.MaxWaitForRisingTarget, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), true) },
                        c => c with { MaxWaitForRisingTarget = StepNullableTimeSpan(c.MaxWaitForRisingTarget, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), false) }),
                ]),

                new ConfigGroup("Focusing",
                [
                    new ConfigFieldDescriptor(
                        "AF Range", ConfigFieldKind.IntStepper, "steps",
                        c => $"{c.AutoFocusRange}",
                        c => c with { AutoFocusRange = Math.Min(c.AutoFocusRange + 10, 500) },
                        c => c with { AutoFocusRange = Math.Max(c.AutoFocusRange - 10, 50) }),
                    new ConfigFieldDescriptor(
                        "AF Step Count", ConfigFieldKind.IntStepper, "",
                        c => $"{c.AutoFocusStepCount}",
                        c => c with { AutoFocusStepCount = Math.Min(c.AutoFocusStepCount + 2, 21) },
                        c => c with { AutoFocusStepCount = Math.Max(c.AutoFocusStepCount - 2, 3) }),
                    new ConfigFieldDescriptor(
                        "Focus Drift Thr.", ConfigFieldKind.FloatStepper, "",
                        c => $"{c.FocusDriftThreshold:0.00}",
                        c => c with { FocusDriftThreshold = MathF.Min(c.FocusDriftThreshold + 0.01f, 2.0f) },
                        c => c with { FocusDriftThreshold = MathF.Max(c.FocusDriftThreshold - 0.01f, 1.01f) }),
                    new ConfigFieldDescriptor(
                        "Focus Filter", ConfigFieldKind.EnumCycle, "",
                        c => c.FocusFilterStrategy.ToString(),
                        c => c with { FocusFilterStrategy = (FocusFilterStrategy)(((int)c.FocusFilterStrategy + 1) % 3) },
                        c => c with { FocusFilterStrategy = (FocusFilterStrategy)(((int)c.FocusFilterStrategy + 2) % 3) }),
                    new ConfigFieldDescriptor(
                        "Refocus on New", ConfigFieldKind.BoolToggle, "",
                        c => c.AlwaysRefocusOnNewTarget ? "ON" : "OFF",
                        c => c with { AlwaysRefocusOnNewTarget = !c.AlwaysRefocusOnNewTarget },
                        c => c with { AlwaysRefocusOnNewTarget = !c.AlwaysRefocusOnNewTarget }),
                    new ConfigFieldDescriptor(
                        "Baseline HFD N", ConfigFieldKind.IntStepper, "",
                        c => $"{c.BaselineHfdFrameCount}",
                        c => c with { BaselineHfdFrameCount = Math.Min(c.BaselineHfdFrameCount + 1, 10) },
                        c => c with { BaselineHfdFrameCount = Math.Max(c.BaselineHfdFrameCount - 1, 1) }),
                ]),

                new ConfigGroup("Imaging",
                [
                    new ConfigFieldDescriptor(
                        "Default Sub Exp", ConfigFieldKind.NullableTimeSpanStepper, "",
                        c => FormatNullableTimeSpan(c.DefaultSubExposure),
                        c => c with { DefaultSubExposure = StepNullableTimeSpan(c.DefaultSubExposure, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), true) },
                        c => c with { DefaultSubExposure = StepNullableTimeSpan(c.DefaultSubExposure, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), false) }),
                ]),

                new ConfigGroup("Mosaic",
                [
                    new ConfigFieldDescriptor(
                        "Mosaic Overlap", ConfigFieldKind.FloatStepper, "",
                        c => $"{c.MosaicOverlap:0.00}",
                        c => c with { MosaicOverlap = Math.Min(c.MosaicOverlap + 0.05, 0.5) },
                        c => c with { MosaicOverlap = Math.Max(c.MosaicOverlap - 0.05, 0.0) }),
                    new ConfigFieldDescriptor(
                        "Mosaic Margin", ConfigFieldKind.FloatStepper, "",
                        c => $"{c.MosaicMargin:0.00}",
                        c => c with { MosaicMargin = Math.Min(c.MosaicMargin + 0.05, 0.5) },
                        c => c with { MosaicMargin = Math.Max(c.MosaicMargin - 0.05, 0.0) }),
                ]),

                new ConfigGroup("Conditions",
                [
                    new ConfigFieldDescriptor(
                        "Deterioration Thr.", ConfigFieldKind.FloatStepper, "",
                        c => $"{c.ConditionDeteriorationThreshold:0.00}",
                        c => c with { ConditionDeteriorationThreshold = MathF.Min(c.ConditionDeteriorationThreshold + 0.05f, 1.0f) },
                        c => c with { ConditionDeteriorationThreshold = MathF.Max(c.ConditionDeteriorationThreshold - 0.05f, 0.1f) }),
                    new ConfigFieldDescriptor(
                        "Recovery Timeout", ConfigFieldKind.NullableTimeSpanStepper, "",
                        c => FormatNullableTimeSpan(c.ConditionRecoveryTimeout),
                        c => c with { ConditionRecoveryTimeout = StepNullableTimeSpan(c.ConditionRecoveryTimeout, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), true) },
                        c => c with { ConditionRecoveryTimeout = StepNullableTimeSpan(c.ConditionRecoveryTimeout, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), false) }),
                ]),
            ];
        }

    }
}
