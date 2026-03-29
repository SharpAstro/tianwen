using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
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

    ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default);

    ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default);

    ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default);

    ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default);

    Float32HxWImageData? ImageData { get; }

    /// <summary>
    /// Signals that the consumer is done with the current image data returned by <see cref="ImageData"/>.
    /// The camera driver may reuse the backing <c>float[,]</c> arrays for the next exposure.
    /// Must be called after FITS writing and any processing that reads the raw pixel data.
    /// </summary>
    void ReleaseImageData();

    /// <summary>
    /// Returns the ref-counted channel buffer for the current image, if the driver supports buffer reuse.
    /// The default method <see cref="GetImageAsync"/> calls <see cref="Imaging.ChannelBuffer.AddRef"/> on this
    /// and attaches it to the returned <see cref="Image"/>. Consumers call <see cref="Image.Release"/> when done.
    /// </summary>
    internal Imaging.ChannelBuffer? ChannelBuffer => null;

    /// <summary>
    /// Returns bit depth, usually <see cref="BitDepth.Int8"/> or <see cref="BitDepth.Int16"/> or <see langword="null"/> if camera is not initialised.
    /// Will throw if <see cref="CanSetBitDepth"/> is <see langword="false" /> and an attempt to set value is made.
    /// </summary>
    ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default);

    ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default);

    ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default);

    ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default);

    short GainMin { get; }

    short GainMax { get; }

    IReadOnlyList<string> Gains { get; }

    async ValueTask<string?> GetGainModeAsync(CancellationToken cancellationToken = default)
    {
        if (Connected && UsesGainMode && Gains.ToList() is { Count: > 0 } gains && await GetGainAsync(cancellationToken) is short idx && idx >= 0 && idx < gains.Count)
        {
            return gains[idx];
        }
        return null;
    }

    async ValueTask SetGainModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (Connected && UsesGainMode && Gains.ToList() is { Count: > 0 } gains && value is not null && gains.IndexOf(value) is var idx and <= short.MaxValue)
        {
            await SetGainAsync((short)idx, cancellationToken);
        }
    }

    ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default);

    ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default);

    int OffsetMin { get; }

    int OffsetMax { get; }

    async ValueTask<string?> GetOffsetModeAsync(CancellationToken cancellationToken = default)
    {
        if (Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && await GetOffsetAsync(cancellationToken) is int idx && idx >= 0 && idx < offsets.Count)
        {
            return offsets[idx];
        }
        return null;
    }

    async ValueTask SetOffsetModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (Connected && UsesOffsetMode && Offsets.ToList() is { Count: > 0 } offsets && value is not null && offsets.IndexOf(value) is var idx)
        {
            await SetOffsetAsync(idx, cancellationToken);
        }
    }

    IReadOnlyList<string> Offsets { get; }

    double ExposureResolution { get; }

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

    ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default);

    ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default);

    ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default);

    ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default);

    ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default);

    ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default);

    ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default);

    ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default);

    ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default);

    ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default);

    /// <summary>
    /// Should only be called when <see cref="CanStopExposure"/> is <see langword="true"/>.
    /// </summary>
    ValueTask StopExposureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Should only be called when <see cref="CanAbortExposure"/> is <see langword="true"/>.
    /// </summary>
    ValueTask AbortExposureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Should only be called when <see cref="CanPulseGuide"/> is <see langword="true"/>.
    /// </summary>
    ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default);

    async ValueTask<Image?> GetImageAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected) return null;
        if (!await GetImageReadyAsync(cancellationToken)) return null;
        if (ImageData is not ({ Length: > 0 }, >= 0, >= 0) imageData) return null;
        if (await GetBitDepthAsync(cancellationToken) is not { } bitDepth || !bitDepth.IsIntegral) return null;
        if (LastExposureStartTime is not { } startTime) return null;

        var ccdTemp = await GetCCDTemperatureAsync(cancellationToken);
        var offset = await GetOffsetAsync(cancellationToken);
        var gain = await GetGainAsync(cancellationToken);
        var setCCDTemp = CanSetCCDTemperature ? (float)await External.CatchAsync(GetSetCCDTemperatureAsync, cancellationToken, double.NaN) : float.NaN;
        float egain;
        try { egain = (float)ElectronsPerADU; } catch { egain = float.NaN; }

        var image = imageData.ToImage(
            bitDepth,
            pedestal: 0f,
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
                (float)ccdTemp,
                SensorType,
                SensorType == SensorType.RGGB ? BayerOffsetX : 0,
                SensorType == SensorType.RGGB ? BayerOffsetY : 0,
                RowOrder.TopDown,
                (float)(Latitude ?? double.NaN),
                (float)(Longitude ?? double.NaN),
                ObjectName: Target?.Name ?? "",
                Gain: gain,
                Offset: offset,
                SetCCDTemperature: setCCDTemp,
                TargetRA: Target?.RA ?? double.NaN,
                TargetDec: Target?.Dec ?? double.NaN,
                ElectronsPerADU: egain,
                SWCreator: External.SWCreator
            )
        );

        // Transfer ownership of the channel buffer to the consumer.
        // Camera drops its ref — the Image is now the sole owner.
        // When consumer calls image.Release(), onRelease fires → camera gets buffer back.
        if (ChannelBuffer is { } buf)
        {
            image.WithChannelBuffers(buf); // transfer camera's ref (no AddRef needed)
            ReleaseImageData(); // camera drops its state — buffer lives in Image only
        }

        return image;
    }

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

    async ValueTask<CameraCoolingState> CoolToSetpointAsync(SetpointTemp desiredSetpointTemp, double thresPower, SetupointDirection direction, CameraCoolingState coolingState, CancellationToken cancellationToken = default)
    {
        if (IsCoolable)
        {
            var ccdTemp = await GetCCDTemperatureAsync(cancellationToken);
            var hasCCDTemp = !double.IsNaN(ccdTemp) && ccdTemp is >= -40 and <= 50;
            var heatSinkTemp = await GetHeatSinkTemperatureAsync(cancellationToken);
            var hasHeatSinkTemp = !double.IsNaN(heatSinkTemp) && heatSinkTemp is >= -40 and <= 50;

            var coolerPower = await External.CatchAsyncIf(CanGetCoolerPower, GetCoolerPowerAsync, cancellationToken, double.NaN);
            // TODO: Consider using external temp sensor if no heatsink temp is available
            var heatSinkOrCCDTemp = hasHeatSinkTemp ? heatSinkTemp : ccdTemp;
            var setpointTemp = desiredSetpointTemp.Kind switch
            {
                SetpointTempKind.Normal => desiredSetpointTemp.TempC,
                SetpointTempKind.CCD when hasCCDTemp && hasHeatSinkTemp => Math.Min(ccdTemp, heatSinkOrCCDTemp),
                SetpointTempKind.CCD when hasCCDTemp && !hasHeatSinkTemp => ccdTemp,
                SetpointTempKind.Ambient when hasHeatSinkTemp => heatSinkTemp,
                _ => double.NaN
            };

            if (double.IsNaN(setpointTemp))
            {
                return new CameraCoolingState(false, 0, false, true);
            }

            CameraCoolingState newState;
            var needsFurtherRamping = direction.NeedsFurtherRamping(ccdTemp, setpointTemp);

            if (needsFurtherRamping
                && (double.IsNaN(coolerPower) || !await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken) || !direction.ThresholdPowerReached(coolerPower, thresPower))
            )
            {
                var actualSetpointTemp = direction.SetpointTemp(ccdTemp, setpointTemp);
                await SetSetCCDTemperatureAsync(actualSetpointTemp, cancellationToken);

                // turn cooler on if required
                string coolerPrev;
                if (await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken))
                {
                    coolerPrev = "";
                }
                else
                {
                    coolerPrev = "off -> ";
                    await SetCoolerOnAsync(true, cancellationToken);
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
                    Name, setpointTemp, direction.ToString().ToLowerInvariant(), actualSetpointTemp, heatSinkTemp, ccdTemp, await External.CatchAsyncIf(CanGetCoolerPower, GetCoolerPowerAsync, cancellationToken, double.NaN), coolerPrev + (await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken) ? "on" : "off")

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
                    Name, setpointTemp, thresPower, heatSinkTemp, ccdTemp, await External.CatchAsyncIf(CanGetCoolerPower, GetCoolerPowerAsync, cancellationToken, double.NaN), await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken) ? "on" : "off");
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
                    Name, setpointTemp, thresPower, heatSinkTemp, ccdTemp, await External.CatchAsyncIf(CanGetCoolerPower, GetCoolerPowerAsync, cancellationToken, double.NaN), await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken) ? "on" : "off");
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
                Name, setpointTemp, await External.CatchAsyncIf(CanGetCoolerPower, GetCoolerPowerAsync, cancellationToken, double.NaN), await External.CatchAsyncIf(CanGetCoolerOn, GetCoolerOnAsync, cancellationToken) ? "on" : "off");

            return new CameraCoolingState(false, 0, false, false);
        }

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
