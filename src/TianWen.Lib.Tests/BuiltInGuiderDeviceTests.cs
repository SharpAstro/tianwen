using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

public class BuiltInGuiderDeviceTests
{
    [Fact]
    public void GivenBareUriThenAdvancedKnobsHaveDefaults()
    {
        var device = new BuiltInGuiderDevice();

        device.MaxCalibrationAttempts.ShouldBe(BuiltInGuiderDevice.DefaultMaxCalibrationAttempts);
        device.MaxRecalibrationAttempts.ShouldBe(BuiltInGuiderDevice.DefaultMaxRecalibrationAttempts);
        device.CalibrationRetryDelay.ShouldBe(TimeSpan.FromSeconds(BuiltInGuiderDevice.DefaultCalibrationRetryDelaySeconds));
        device.NeuralSettleFailSafeFraction.ShouldBe(BuiltInGuiderDevice.DefaultNeuralSettleFailSafeFraction);
    }

    [Fact]
    public void GivenQueryParamsThenAdvancedKnobsParse()
    {
        var uri = new BuiltInGuiderDevice().DeviceUri.WithQueryValues(
            (DeviceQueryKey.MaxCalibrationAttempts.Key, "5"),
            (DeviceQueryKey.MaxRecalibrationAttempts.Key, "1"),
            (DeviceQueryKey.CalibrationRetryDelaySeconds.Key, "20"),
            (DeviceQueryKey.NeuralSettleFailSafeFraction.Key, "0.75"));
        var device = new BuiltInGuiderDevice(uri);

        device.MaxCalibrationAttempts.ShouldBe(5);
        device.MaxRecalibrationAttempts.ShouldBe(1);
        device.CalibrationRetryDelay.ShouldBe(TimeSpan.FromSeconds(20));
        device.NeuralSettleFailSafeFraction.ShouldBe(0.75);
    }

    [Fact]
    public void GivenSettingsThenAdvancedRowsAreMarkedAdvanced()
    {
        var device = new BuiltInGuiderDevice();
        var advancedKeys = device.Settings.Where(s => s.IsAdvanced).Select(s => s.Key).ToArray();

        advancedKeys.ShouldBe([
            DeviceQueryKey.MaxCalibrationAttempts.Key,
            DeviceQueryKey.MaxRecalibrationAttempts.Key,
            DeviceQueryKey.CalibrationRetryDelaySeconds.Key,
            DeviceQueryKey.NeuralSettleFailSafeFraction.Key,
        ]);

        // The everyday settings stay in the basic (always visible) section.
        device.Settings.Where(s => !s.IsAdvanced).Select(s => s.Key).ShouldBe([
            DeviceQueryKey.ReuseCalibration.Key,
            DeviceQueryKey.PulseGuideSource.Key,
            DeviceQueryKey.ReverseDecAfterFlip.Key,
            DeviceQueryKey.UseNeuralGuider.Key,
            DeviceQueryKey.NeuralBlendFactor.Key,
        ]);
    }
}
