using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Ascom;

public class AscomCameraDriver : AscomDeviceDriverBase, ICameraDriver
{
    public AscomCameraDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        DeviceConnectedEvent += AscomCameraDriver_DeviceConnectedEvent;
    }

    private void AscomCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected && _comObject is { } obj)
        {
            CanGetCoolerPower = obj.CanGetCoolerPower is bool canGetCoolerPower && canGetCoolerPower;
            CanSetCCDTemperature = obj.CanSetCCDTemperature is bool canSetCCDTemperature && canSetCCDTemperature;
            CanStopExposure = obj.CanStopExposure is bool canStopExposure && canStopExposure;
            CanAbortExposure = obj.CanAbortExposure is bool canAbortExposure && canAbortExposure;
            CanFastReadout = obj.CanFastReadout is bool canFastReadout && canFastReadout;
            CanPulseGuide = obj.CanPulseGuide is bool canPulseGuide && canPulseGuide;

            try
            {
                _ = obj.CoolerOn;
                CanGetCoolerOn = true;
                CanSetCoolerOn = true;
            }
            catch
            {
                CanGetCoolerOn = false;
                CanSetCoolerOn = false;
            }

            try
            {
                CanGetHeatsinkTemperature = !double.IsNaN(HeatSinkTemperature);
            }
            catch
            {
                CanGetHeatsinkTemperature = false;
            }

            try
            {
                CanGetCCDTemperature = !double.IsNaN(CCDTemperature);
            }
            catch
            {
                CanGetCCDTemperature = false;
            }

            if (obj.InterfaceVersion is int and >= 3)
            {
                try
                {
                    _ = obj.Offset;
                    var min = obj.OffsetMin;
                    var max = obj.OffsetMax;
                    UsesOffsetValue = true;
                    OffsetMin = min;
                    OffsetMax = max;
                }
                catch
                {
                    UsesOffsetValue = false;
                    OffsetMin = int.MinValue;
                    OffsetMax = int.MinValue;
                }

                if (!UsesOffsetValue)
                {
                    try
                    {
                        _ = obj.Offset;
                        _ = obj.Offsets;
                        UsesOffsetMode = true;
                    }
                    catch
                    {
                        UsesOffsetMode = false;
                    }
                }
            }
            else
            {
                UsesOffsetValue = false;
                UsesOffsetMode = false;
            }

            try
            {
                _ = obj.Gain;
                var min = obj.GainMin;
                var max = obj.GainMax;
                UsesGainValue = true;
                GainMin = min;
                GainMax = max;
            }
            catch
            {
                UsesGainValue = false;
                GainMin = short.MinValue;
                GainMax = short.MinValue;
            }

            if (!UsesGainValue)
            {
                try
                {
                    _ = obj.Gain;
                    _ = obj.Gains;
                    UsesGainMode = true;
                }
                catch
                {
                    UsesGainMode = false;
                }
            }
        }
    }

    public bool CanGetCoolerPower { get; private set; }

    public bool CanGetCoolerOn { get; private set; }
    public bool CanSetCoolerOn { get; private set; }

    public bool CanGetHeatsinkTemperature { get; private set; }

    public bool CanGetCCDTemperature { get; private set; }

    public bool CanSetCCDTemperature { get; private set; }

    public bool CanStopExposure { get; private set; }

    public bool CanAbortExposure { get; private set; }

    public bool CanFastReadout { get; private set; }

    public bool CanSetBitDepth { get; } = false;

    public bool CanPulseGuide { get; private set; }

    public bool UsesGainValue { get; private set; }

    public bool UsesGainMode { get; private set; }

    public bool UsesOffsetValue { get; private set; }

    public bool UsesOffsetMode { get; private set; }

    public double CoolerPower => Connected && _comObject?.CoolerPower is double coolerPower ? coolerPower :throw new InvalidOperationException("Camera is not connected");

    public double HeatSinkTemperature => Connected && _comObject?.HeatSinkTemperature is double heatSinkTemperature ? heatSinkTemperature :throw new InvalidOperationException("Camera is not connected");

    public double CCDTemperature => Connected && _comObject?.CCDTemperature is double ccdTemperature ? ccdTemperature :throw new InvalidOperationException("Camera is not connected");

    public double PixelSizeX => Connected && _comObject?.PixelSizeX is double pixelSizeX ? pixelSizeX :throw new InvalidOperationException("Camera is not connected");

    public double PixelSizeY => Connected && _comObject?.PixelSizeY is double pixelSizeY ? pixelSizeY :throw new InvalidOperationException("Camera is not connected");

    public int StartX
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.StartX;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.StartX = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int StartY
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.StartY;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.StartY = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int BinX
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.BinX;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.BinX = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int BinY
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.BinY;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.BinY = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public short MaxBinX
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.MaxBinX;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public short MaxBinY
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.MaxBinY;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int NumX
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.NumX;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.NumX = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int NumY
    {
        get
        {
            if (Connected && _comObject is { } obj)
            {
                return obj.NumY;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }

        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.NumY = value;
            }
            else
            {
                throw new InvalidOperationException("Camera is not connected");
            }
        }
    }

    public int CameraXSize => Connected && _comObject?.CameraXSize is int xSize ? xSize : throw new InvalidOperationException("Camera is not connected");

    public int CameraYSize => Connected && _comObject?.CameraYSize is int ySize ? ySize : throw new InvalidOperationException("Camera is not connected");

    public int Offset
    {
        get
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (_comObject?.InterfaceVersion is >= 3 && _comObject?.Offset is int offset)
            {
                return offset;
            }
            else
            {
                throw new InvalidOperationException("Offset property is not supported");
            }
        }

        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (_comObject is { } obj && obj.InterfaceVersion is >= 3)
            {
                obj.Offset = value;
            }
            else
            {
                throw new InvalidOperationException("Offset property is not supported");
            }
        }
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IEnumerable<string> Offsets => Connected && UsesOffsetMode && _comObject is { } obj ? EnumerateProperty<string>(obj.Offsets) : Enumerable.Empty<string>();

    public short Gain
    {
        get => Connected && _comObject?.Gain is short gain ? gain : throw new InvalidOperationException("Camera is not connected");

        set
        {
            if (Connected && _comObject is { } obj)
            {
               obj.Gain = value;
            }
        }
    }

    public short GainMin { get; private set; }

    public short GainMax { get; private set; }

    public IEnumerable<string> Gains => Connected && UsesGainMode && _comObject is { } obj ? EnumerateProperty<string>(obj.Gains) : Enumerable.Empty<string>();

    public bool FastReadout
    {
        get => Connected && CanFastReadout && _comObject?.FastReadout is bool fastReadout ? fastReadout : throw new InvalidOperationException("Camera is not connected");
        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (CanFastReadout && _comObject is { } obj)
            {
                obj.FastReadout = value;
            }
            else
            {
                throw new InvalidOperationException("Fast readout is not supported");
            }
        }
    }

    private IReadOnlyList<string> ReadoutModes
        => Connected && _comObject is { } obj && EnumerateProperty<string>(obj.ReadoutModes) is IEnumerable<string> modes ? modes.ToList() : throw new InvalidOperationException("Camera is not connected");

    public string? ReadoutMode
    {
        get => _comObject?.ReadoutMode is int readoutMode && readoutMode >= 0 && ReadoutModes is { Count: > 0 } modes && readoutMode < modes.Count ? modes[readoutMode] : null;
        set
        {
            int idx;
            if (Connected && _comObject is { } obj && value is { Length: > 0 } && (idx = ReadoutModes.IndexOf(value)) >= 0)
            {
                obj.ReadoutMode = idx;
            }
        }
    }

    public Float32HxWImageData? ImageData => Connected && _comObject?.ImageArray is int[,] intArray ? Float32HxWImageData.FromWxHImageData(intArray) : null;

    public bool ImageReady => Connected && _comObject is { } obj ? (bool)obj.ImageReady : throw new InvalidOperationException("Camera is not connected");

    public bool IsPulseGuiding => Connected && _comObject is { } obj ? (bool)obj.IsPulseGuiding : throw new InvalidOperationException("Camera is not connected");

    public int MaxADU => Connected && _comObject is { } obj ? (int)obj.MaxADU : throw new InvalidOperationException("Camera is not connected");

    public double FullWellCapacity => Connected && _comObject?.FullWellCapacity is double fullWellCapacity ? fullWellCapacity :throw new InvalidOperationException("Camera is not connected");

    public double ElectronsPerADU => Connected && _comObject?.ElectronsPerADU is { } elecPerADU ? elecPerADU :throw new InvalidOperationException("Camera is not connected");

    public DateTimeOffset? LastExposureStartTime { get; private set; }

    public TimeSpan? LastExposureDuration
        => Connected && _comObject?.LastExposureDuration is double lastExposureDuration
            ? TimeSpan.FromSeconds(lastExposureDuration)
            : default;

    public FrameType LastExposureFrameType { get; internal set; }

    public BitDepth? BitDepth
    {
        get
        {
            var maxADU = MaxADU;
            if (maxADU is <= 0 || double.IsNaN(FullWellCapacity))
            {
                return null;
            }

            if (maxADU == byte.MaxValue && MaxADU < FullWellCapacity && Name.Contains("QHYCCD", StringComparison.OrdinalIgnoreCase))
            {
                maxADU = (int)FullWellCapacity;
            }

            int log2 = (int)MathF.Ceiling(MathF.Log(maxADU) / MathF.Log(2.0f));
            var bytesPerPixel = (log2 + 7) / 8;
            int bitDepth = bytesPerPixel * 8;

            return BitDepthEx.FromValue(bitDepth);
        }

        set => throw new InvalidOperationException("Setting bit depth is not supported!");
    }

    public double ExposureResolution => Connected && _comObject?.ExposureResolution is double expResolution ? expResolution :throw new InvalidOperationException("Camera is not connected");

    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType)
    {
        _comObject?.StartExposure(duration.TotalSeconds, frameType.NeedsOpenShutter());
        var startTime = External.TimeProvider.GetLocalNow();
        LastExposureStartTime = startTime;
        LastExposureFrameType = frameType;
        return startTime;
    }

    public void StopExposure()
    {
        if (CanStopExposure && Connected && _comObject is { } obj)
        {
            obj.StopExposure();
        }
        else
        {
            throw new InvalidOperationException("Failed to stop exposure");
        }
    }

    public void AbortExposure()
    {
        if (CanAbortExposure && Connected && _comObject is { } obj)
        {
            obj.AbortExposure();
        }
        else
        {
            throw new InvalidOperationException("Failed to abort exposure");
        }
    }

    public void PulseGuide(GuideDirection direction, TimeSpan duration)
    {
        var durationMs = (int)duration.Round(TimeSpanRoundingType.Millisecond).TotalMilliseconds;

        if (Connected && _comObject is { } obj)
        {
            obj.PulseGuide(direction, durationMs);
        }
        else
        {
            throw new InvalidOperationException("Camera is not connected, cannot pulse guide");
        }
    }

    public bool CoolerOn
    {
        get => Connected && _comObject?.CoolerOn is bool coolerOn && coolerOn;
        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.CoolerOn = value;
            }
        }
    }

    public double SetCCDTemperature
    {
        get => Connected && CanSetCCDTemperature && _comObject?.SetCCDTemperature is double setCCDTemperature ? setCCDTemperature :throw new InvalidOperationException("Camera is not connected");
        set
        {
            if (Connected && CanSetCCDTemperature && _comObject is { } obj)
            {
                obj.SetCCDTemperature = value;
            }
        }
    }

    public CameraState CameraState => Connected && _comObject?.CameraState is int cs ? (CameraState)cs : CameraState.NotConnected;

    public SensorType SensorType => Connected && _comObject?.SensorType is int st ? (SensorType)st : SensorType.Unknown;

    public int BayerOffsetX => Connected && _comObject?.BayerOffsetX is int bayerOffsetX ? bayerOffsetX : 0;

    public int BayerOffsetY => Connected && _comObject?.BayerOffsetY is int bayerOffsetY ? bayerOffsetY : 0;

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int FocusPosition { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public Target? Target { get; set; }
    #endregion
}