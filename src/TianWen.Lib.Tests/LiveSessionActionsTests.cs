using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the preview-mode helpers of <see cref="LiveSessionActions"/>: the exposure and gain steppers and their labels.
/// The device reads and moves they once held are the node's since P6 (docs/plans/hardware-in-the-server.md, #936).
/// </summary>
public class LiveSessionActionsTests
{
    // ── StepExposure ──

    [Theory]
    [InlineData(5.0, 1, 10.0)]     // middle-of-ladder up
    [InlineData(5.0, -1, 3.0)]     // middle-of-ladder down
    [InlineData(0.1, -1, 0.1)]     // clamp at min
    [InlineData(300, 1, 300)]      // clamp at max
    [InlineData(2.5, 1, 3.0)]      // off-ladder → snaps to next above
    [InlineData(2.5, -1, 2.0)]     // off-ladder → snaps to next below
    [InlineData(5.0, 0, 5.0)]      // direction 0 is a no-op
    public void StepExposure_WalksTheLadderAndClampsAtEnds(double cur, int direction, double expected)
    {
        LiveSessionActions.StepExposure(cur, direction).ShouldBe(expected);
    }

    [Fact]
    public void PreviewExposureSteps_IsStrictlyAscending()
    {
        var steps = LiveSessionActions.PreviewExposureSteps;
        for (var i = 1; i < steps.Length; i++)
        {
            steps[i].ShouldBeGreaterThan(steps[i - 1]);
        }
    }

    // ── StepGain ──

    private static PreviewOTATelemetry NumericGainTel(short min = 0, short max = 300, short current = 120)
        => PreviewOTATelemetry.Unknown with { UsesGainValue = true, GainMin = min, GainMax = max, CurrentGain = current };

    private static PreviewOTATelemetry ModeGainTel(params string[] modes)
        => PreviewOTATelemetry.Unknown with { UsesGainMode = true, GainModes = [.. modes], CurrentGain = 1 };

    [Fact]
    public void StepGain_NumericUp_MovesByDynamicStep()
    {
        // range 0..300 -> step = max(1, 300/20) = 15
        LiveSessionActions.StepGain(100, NumericGainTel(), direction: +1).ShouldBe(115);
    }

    [Fact]
    public void StepGain_NumericDown_MovesByDynamicStep()
    {
        LiveSessionActions.StepGain(100, NumericGainTel(), direction: -1).ShouldBe(85);
    }

    [Fact]
    public void StepGain_NumericWithNullCurrent_StartsFromCameraDefault()
    {
        // current=null -> seed from tel.CurrentGain (120), step +15 = 135
        LiveSessionActions.StepGain(null, NumericGainTel(current: 120), direction: +1).ShouldBe(135);
    }

    [Fact]
    public void StepGain_NumericClampsToMax()
    {
        LiveSessionActions.StepGain(295, NumericGainTel(0, 300), direction: +1).ShouldBe(300);
    }

    [Fact]
    public void StepGain_NumericClampsToMin()
    {
        LiveSessionActions.StepGain(5, NumericGainTel(0, 300), direction: -1).ShouldBe(0);
    }

    [Fact]
    public void StepGain_NumericWithNarrowRange_UsesStepOfOne()
    {
        // range 10..15 -> (15-10)/20 = 0, clamped to 1
        LiveSessionActions.StepGain(12, NumericGainTel(10, 15, 12), direction: +1).ShouldBe(13);
    }

    [Fact]
    public void StepGain_ModeUp_IncrementsIndex()
    {
        var tel = ModeGainTel("ISO100", "ISO200", "ISO400", "ISO800");
        LiveSessionActions.StepGain(1, tel, direction: +1).ShouldBe(2);
    }

    [Fact]
    public void StepGain_ModeClampsAtEnds()
    {
        var tel = ModeGainTel("ISO100", "ISO200", "ISO400");
        LiveSessionActions.StepGain(2, tel, direction: +1).ShouldBe(2);  // max
        LiveSessionActions.StepGain(0, tel, direction: -1).ShouldBe(0);  // min
    }

    [Fact]
    public void StepGain_NoGainCapabilities_ReturnsEffectiveUnchanged()
    {
        var tel = PreviewOTATelemetry.Unknown with { CurrentGain = 42 };
        LiveSessionActions.StepGain(null, tel, direction: +1).ShouldBe(42);
        LiveSessionActions.StepGain(100, tel, direction: +1).ShouldBe(100);
    }

    // ── FormatExposureLabel ──

    [Theory]
    [InlineData(0.1, "0.1s")]
    [InlineData(0.5, "0.5s")]
    [InlineData(1.0, "1s")]
    [InlineData(30.0, "30s")]
    [InlineData(59.9, "59.9s")]
    [InlineData(60.0, "1m")]
    [InlineData(120.0, "2m")]
    [InlineData(300.0, "5m")]
    public void FormatExposureLabel_CrossesSecondsMinuteBoundary(double sec, string expected)
    {
        LiveSessionActions.FormatExposureLabel(sec).ShouldBe(expected);
    }

    // ── FormatGainLabel ──

    [Fact]
    public void FormatGainLabel_NumericWithOverride_ShowsPlainValue()
    {
        LiveSessionActions.FormatGainLabel(150, NumericGainTel()).ShouldBe("Gain: 150");
    }

    [Fact]
    public void FormatGainLabel_NumericWithoutOverride_BracketsCameraDefault()
    {
        LiveSessionActions.FormatGainLabel(null, NumericGainTel(current: 120)).ShouldBe("Gain: (120)");
    }

    [Fact]
    public void FormatGainLabel_ModeWithOverride_ShowsModeName()
    {
        var tel = ModeGainTel("ISO100", "ISO200", "ISO400");
        LiveSessionActions.FormatGainLabel(2, tel).ShouldBe("ISO400");
    }

    [Fact]
    public void FormatGainLabel_ModeWithoutOverride_ParenthesisesCameraDefault()
    {
        var tel = ModeGainTel("ISO100", "ISO200", "ISO400"); // CurrentGain = 1
        LiveSessionActions.FormatGainLabel(null, tel).ShouldBe("(ISO200)");
    }

    [Fact]
    public void FormatGainLabel_ModeOutOfRange_FallsBackToHashIndex()
    {
        var tel = ModeGainTel("ISO100", "ISO200"); // only indices 0, 1 valid
        LiveSessionActions.FormatGainLabel(7, tel).ShouldBe("#7");
    }

    [Fact]
    public void FormatGainLabel_NoGainCapabilities_ReturnsEmpty()
    {
        LiveSessionActions.FormatGainLabel(100, PreviewOTATelemetry.Unknown).ShouldBe(string.Empty);
    }
}
