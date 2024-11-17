using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool CanGetCoolerPower { get; }

    bool CanGetCoolerOn { get; }

    bool CanSetCoolerOn { get; }

    bool CanGetCCDTemperature { get; }

    bool CanSetCCDTemperature { get; }

    bool CanGetHeatsinkTemperature { get; }

    bool CanStopExposure { get; }

    bool CanAbortExposure { get; }

    bool CanFastReadout { get; }

    bool CanSetBitDepth { get; }

    bool CanPulseGuide { get; }

    /// <summary>
    /// True if <see cref="Gain"/> value is supported. Exclusive with <see cref="UsesGainMode"/>.
    /// </summary>
    bool UsesGainValue { get; }

    /// <summary>
    /// True if <see cref="Gain"/> mode is supported. Exclusive with <see cref="UsesGainValue"/>.
    /// </summary>
    bool UsesGainMode { get; }

    /// <summary>
    /// True if <see cref="Offset"/> value mode is supported. Exclusive with  <see cref="UsesOffsetMode"/>.
    /// </summary>
    bool UsesOffsetValue { get; }

    /// <summary>
    /// True if <see cref="Offset"/> mode is supported. Exclusive with <see cref="UsesOffsetValue"/>.
    /// </summary>
    bool UsesOffsetMode { get; }

    double PixelSizeX { get; }

    double PixelSizeY { get; }

    /// <summary>
    /// Returns the maximum allowed binning for the X camera axis.
    /// </summary>
    short MaxBinX { get; }

    /// <summary>
    /// Returns the maximum allowed binning for the Y camera axis.
    /// </summary>
    short MaxBinY { get; }

    /// <summary>
    /// Sets the binning factor for the X axis, also returns the current value.
    /// </summary>
    int BinX { get; set; }

    /// <summary>
    /// Sets the binning factor for the Y axis, also returns the current value.
    /// </summary>
    int BinY { get; set; }

    /// <summary>
    /// Sets the subframe start position for the X axis (0 based) and returns the current value.
    /// </summary>
    int StartX { get; set; }

    /// <summary>
    /// Sets the subframe start position for the Y axis (0 based). Also returns the current value.
    /// </summary>
    int StartY { get; set; }

    /// <summary>
    /// Sets the subframe width (in binned pixels). Also returns the current value.
    /// </summary>
    int NumX { get; set; }

    /// <summary>
    /// Sets the subframe height (in binned pixels). Also returns the current value.
    /// </summary>
    int NumY { get; set; }

    /// <summary>
    /// Returns the width of the camera chip in unbinned pixels or a value smaller than 0 when not initialised.
    /// </summary>
    int CameraXSize { get; }

    /// <summary>
    /// Returns the height of the camera chip in unbinned pixels or a value smaller than 0 when not initialised.
    /// </summary>
    int CameraYSize { get; }

    string? ReadoutMode { get; set; }

    bool FastReadout { get; set; }

    Float32HxWImageData? ImageData { get; }

    bool ImageReady { get; }

    bool IsPulseGuiding { get; }

    bool CoolerOn { get; set; }

    double CoolerPower { get; }

    /// <summary>
    /// Sets the camera cooler setpoint in degrees Celsius, and returns the current setpoint.
    /// </summary>
    double SetCCDTemperature { get; set; }

    /// <summary>
    /// Returns the current heat sink temperature (called "ambient temperature" by some manufacturers) in degrees Celsius.
    /// </summary>
    double HeatSinkTemperature { get; }

    /// <summary>
    /// Returns the current CCD temperature in degrees Celsius.
    /// </summary>
    double CCDTemperature { get; }

    /// <summary>
    /// Returns bit depth, usually <see cref="BitDepth.Int8"/> or <see cref="BitDepth.Int16"/> or <see langword="null"/> if camera is not initialised.
    /// Will throw if <see cref="CanSetBitDepth"/> is <see langword="false" /> and an attempt to set value is made.
    /// </summary>
    BitDepth? BitDepth { get; set; }

    short Gain { get; set; }

    short GainMin { get; }

    short GainMax { get; }

    IEnumerable<string> Gains { get; }

    string? GainMode
    {
        get => Connected && UsesGainMode && Gains.ToList() is { Count: > 0 } gains && Gain is short idx && idx >= 0 && idx < gains.Count
            ? gains[idx]
            : null;

        set
        {
            if (Connected && UsesGainMode && Gains.ToList() is { Count: > 0} gains && value is not null && gains.IndexOf(value) is var idx and <= short.MaxValue)
            {
                Gain = (short)idx;
            }
        }
    }

    int Offset { get; set; }

    int OffsetMin { get; }

    int OffsetMax { get; }

    string? OffsetMode
    {
        get => Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && Offset is int idx && idx >= 0 && idx < offsets.Count
            ? offsets[idx]
            : null;

        set
        {
            if (Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && value is not null && offsets.IndexOf(value) is var idx)
            {
                Offset = idx;
            }
        }
    }

    IEnumerable<string> Offsets { get; }

    double ExposureResolution { get; }

    DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType = FrameType.Light);

    /// <summary>
    /// Should only be called when <see cref="CanStopExposure"/> is <see langword="true"/>.
    /// </summary>
    void StopExposure();

    /// <summary>
    /// Should only be called when <see cref="CanAbortExposure"/> is <see langword="true"/>.
    /// </summary>
    void AbortExposure();

    /// <summary>
    /// Should only be called when <see cref="CanPulseGuide"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="direction"></param>
    /// <param name="duration"></param>
    void PulseGuide(GuideDirection direction, TimeSpan duration);

    /// <summary>
    /// Reports the maximum ADU value the camera can produce.
    /// </summary>
    int MaxADU { get; }

    /// <summary>
    /// Reports the full well capacity of the camera in electrons, at the current camera settings (binning, SetupDialog settings, etc.)
    /// </summary>
    double FullWellCapacity { get; }

    /// <summary>
    /// Returns the gain of the camera in photoelectrons per A/D unit.
    /// </summary>
    double ElectronsPerADU { get; }

    DateTimeOffset? LastExposureStartTime { get; }

    TimeSpan? LastExposureDuration { get; }

    FrameType LastExposureFrameType { get; }

    SensorType SensorType { get; }

    int BayerOffsetX { get; }

    int BayerOffsetY { get; }

    CameraState CameraState { get; }

    Image? Image => Connected
        && ImageReady
        && ImageData is ({ Length: > 0 }, >= 0) imageData
        && BitDepth is { } bitDepth && bitDepth.IsIntegral()
        && LastExposureStartTime is { } startTime
        ? imageData.ToImage(
            bitDepth,
            Offset,
            new ImageMeta(
                Name,
                startTime,
                LastExposureDuration ?? TimeSpan.Zero,
                LastExposureFrameType,
                Telescope ?? "",
                (float)PixelSizeX,
                (float)PixelSizeY,
                FocalLength,
                FocusPosition,
                Filter,
                BinX,
                BinY,
                (float)CCDTemperature,
                SensorType,
                SensorType != SensorType.Monochrome ? BayerOffsetX : 0,
                SensorType != SensorType.Monochrome ? BayerOffsetY : 0,
                RowOrder.TopDown,
                (float)(Latitude ?? double.NaN),
                (float)(Longitude ?? double.NaN)
            )
        )
        : null;

    #region Image metadata
    string? Telescope { get; set; }

    int FocalLength { get; set; }

    double? Latitude { get; set; }

    double? Longitude { get; set; }

    Filter Filter { get; set; }

    int FocusPosition { get; set; }

    Target? Target { get; set; }
    #endregion

    /// <summary>
    /// A coolable camera is a camera that is:
    /// <list type="bullet">
    ///   <item>connected</item>
    ///   <item>can get/set cooler power state</item>
    ///   <item>can get either heat-sink or CCD temperature</item>
    /// </list>
    /// Should not throw an exception.
    /// </summary>
    /// <returns>true if camera is a coolable camera</returns>
    bool IsCoolable =>
        External.Catch(() => Connected)
        && CanSetCCDTemperature
        && CanGetCoolerOn
        && CanSetCoolerOn
        && (CanGetHeatsinkTemperature || CanGetCCDTemperature);

    CameraCoolingState CoolToSetpoint(SetpointTemp desiredSetpointTemp, double thresPower, SetupointDirection direction, CameraCoolingState coolingState)
    {
        if (IsCoolable)
        {
            var ccdTemp = CCDTemperature;
            var hasCCDTemp = !double.IsNaN(ccdTemp) && ccdTemp is >= -40 and <= 50;
            var heatSinkTemp = HeatSinkTemperature;
            var hasHeatSinkTemp = !double.IsNaN(heatSinkTemp) && heatSinkTemp is >= -40 and <= 50;

            var coolerPower = CoolerPowerSafe();
            // TODO: Consider using external temp sensor if no heatsink temp is available
            var heatSinkOrCCDTemp = hasHeatSinkTemp ? heatSinkTemp : ccdTemp;
            var setpointTemp = desiredSetpointTemp.Kind switch
            {
                SetpointTempKind.Normal => desiredSetpointTemp.TempC,
                SetpointTempKind.CCD when hasCCDTemp && hasHeatSinkTemp => Math.Min(ccdTemp, heatSinkOrCCDTemp),
                SetpointTempKind.CCD when hasCCDTemp && !hasHeatSinkTemp => ccdTemp,
                SetpointTempKind.Ambient when hasHeatSinkTemp => ccdTemp,
                _ => double.NaN
            };

            if (double.IsNaN(setpointTemp))
            {
                return new CameraCoolingState(false, 0, false, true);
            }

            CameraCoolingState newState;
            var needsFurtherRamping = direction.NeedsFurtherRamping(ccdTemp, setpointTemp);

            if (needsFurtherRamping
                && (double.IsNaN(coolerPower) || !IsCoolerOnSafe() || !direction.ThresholdPowerReached(coolerPower, thresPower))
            )
            {
                var actualSetpointTemp = SetCCDTemperature = direction.SetpointTemp(ccdTemp, setpointTemp);

                // turn cooler on if required
                string coolerPrev;
                if (IsCoolerOnSafe())
                {
                    coolerPrev = "";
                }
                else
                {
                    coolerPrev = "off -> ";
                    CoolerOn = true;
                }

                newState = coolingState with
                {
                    IsRamping = true,
                    ThresholdReachedConsecutiveCounts = 0,
                    TargetSetpointReached = !needsFurtherRamping,
                    IsCoolable = true
                };

                External.AppLogger.LogInformation("Camera {Name} setpoint temperature {SetpointTemp:0.00} °C not yet reached, " +
                    "cooling {Direction} stepwise, currently at {ActualSetpointTemp:0.00} °C. " +
                    "Heatsink={HeatSinkTemp:0.00} °C, CCD={CCDTemp:0.00} °C, Power={CoolerPower:0.00}%, Cooler={CoolerStateChange}.",
                    Name, setpointTemp, direction.ToString().ToLowerInvariant(), actualSetpointTemp, heatSinkTemp, ccdTemp, CoolerPowerSafe(), coolerPrev + (IsCoolerOnSafe() ? "on" : "off")

                );
            }
            else if (coolingState.ThresholdReachedConsecutiveCounts < 3)
            {
                newState = coolingState with
                {
                    IsRamping = true,
                    ThresholdReachedConsecutiveCounts = coolingState.ThresholdReachedConsecutiveCounts + 1,
                    TargetSetpointReached = !needsFurtherRamping,
                    IsCoolable = true
                };

                External.AppLogger.LogInformation("Camera {Name} setpoint temperature {SetpointTemp:0.00} °C or {ThresPower:0.00} % power reached. Heatsink={HeatSinkTemp:0.00} °C, CCD={CCDTemp:0.00} °C, Power={CoolerPower:0.00}%, Cooler={CoolerState}.",
                    Name, setpointTemp, thresPower, heatSinkTemp, ccdTemp, CoolerPowerSafe(), IsCoolerOnSafe() ? "on" : "off");
            }
            else
            {
                newState = coolingState with
                {
                    IsRamping = false,
                    TargetSetpointReached = !needsFurtherRamping,
                    IsCoolable = true
                };

                External.AppLogger.LogInformation("Camera {Name} setpoint temperature {SetpointTemp:0.00} °C or {ThresPower:0.00} % power reached twice in a row. Heatsink={HeatSinkTemp:0.00} °C, CCD={CCDTemp:0.00} °C, Power={CoolerPower:0.00}%, Cooler={CoolerState}.",
                    Name, setpointTemp, thresPower, heatSinkTemp, ccdTemp, CoolerPowerSafe(), IsCoolerOnSafe() ? "on" : "off");
            }

            return newState;
        }
        else
        {
            var setpointTemp = desiredSetpointTemp.Kind switch
            {
                SetpointTempKind.Ambient => "ambient",
                SetpointTempKind.CCD => "current sensor",
                _ => $"{desiredSetpointTemp.TempC:0.00} °C"
            };
            External.AppLogger.LogWarning("Skipping camera {Name} setpoint temperature {setpointTemp} as we cannot get the current CCD temperature or cooling is not supported. Power={CoolerPower:0.00}%, Cooler={CoolerState}.",
                Name, setpointTemp, CoolerPowerSafe(), IsCoolerOnSafe() ? "on" : "off");

            return new CameraCoolingState(false, 0, false, false);
        }

        bool IsCoolerOnSafe() => External.Catch(() => CanGetCoolerOn && CoolerOn);

        double CoolerPowerSafe() => External.Catch(() => CanGetCoolerPower ? CoolerPower : double.NaN, double.NaN);
    }
}
public record struct CameraCoolingState(bool IsRamping, int ThresholdReachedConsecutiveCounts, bool? TargetSetpointReached, bool? IsCoolable);

public enum SetpointTempKind : byte
{
    Normal,
    CCD,
    Ambient
}

public record struct SetpointTemp(sbyte TempC, SetpointTempKind Kind);
