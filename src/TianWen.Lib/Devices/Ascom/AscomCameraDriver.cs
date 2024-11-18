using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using AscomCamera = ASCOM.Com.DriverAccess.Camera;
using AscomGuideDirection = ASCOM.Common.DeviceInterfaces.GuideDirection;

namespace TianWen.Lib.Devices.Ascom;

internal class AscomCameraDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomCamera>(device, external, (progId, logger) => new AscomCamera(progId, new AscomLoggerWrapper(logger))), ICameraDriver
{
    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        CanGetCoolerPower = _comObject.CanGetCoolerPower;
        CanSetCCDTemperature = _comObject.CanSetCCDTemperature;
        CanStopExposure = _comObject.CanStopExposure;
        CanAbortExposure = _comObject.CanAbortExposure;
        CanFastReadout = _comObject.CanFastReadout;
        CanPulseGuide = _comObject.CanPulseGuide;

        try
        {
            _ = _comObject.CoolerOn;
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

        if (_comObject.InterfaceVersion is >= 3)
        {
            try
            {
                _ = _comObject.Offset;
                var min = _comObject.OffsetMin;
                var max = _comObject.OffsetMax;
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
                    _ = _comObject.Offset;
                    _ = _comObject.Offsets;
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
            _ = _comObject.Gain;
            var min = _comObject.GainMin;
            var max = _comObject.GainMax;
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
                _ = _comObject.Gain;
                _ = _comObject.Gains;
                UsesGainMode = true;
            }
            catch
            {
                UsesGainMode = false;
            }
        }

        return ValueTask.FromResult(true);
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

    public double HeatSinkTemperature => _comObject.HeatSinkTemperature;

    public double CCDTemperature => _comObject.CCDTemperature;

    public double PixelSizeX => _comObject.PixelSizeX;

    public double PixelSizeY => _comObject.PixelSizeY;

    public int StartX
    {
        get => _comObject.StartX;
        set => _comObject.StartX = value;
    }

    public int StartY
    {
        get => _comObject.StartY;
        set => _comObject.StartY = value;
    }

    public int BinX
    {
        get => _comObject.BinX;
        set => _comObject.BinX = (short)value;
    }

    public int BinY
    {
        get => _comObject.BinY;
        set => _comObject.BinY = (short)value;
    }

    public short MaxBinX => _comObject.MaxBinX;

    public short MaxBinY => _comObject.MaxBinY;

    public int NumX
    {
        get => _comObject.NumX;
        set => _comObject.NumX = value;
    }

    public int NumY
    {
        get => _comObject.NumY;
        set => _comObject.NumY = value;
    }

    public int CameraXSize => Connected && _comObject?.CameraXSize is int xSize ? xSize : throw new InvalidOperationException("Camera is not connected");

    public int CameraYSize => Connected && _comObject?.CameraYSize is int ySize ? ySize : throw new InvalidOperationException("Camera is not connected");

    public int Offset
    {
        get => _comObject.Offset;
        set => _comObject.Offset = value;
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IReadOnlyList<string> Offsets => Connected && UsesOffsetMode && _comObject.Offsets is { } offsets ? offsets.AsReadOnly() : [];

    public short Gain
    {
        get => _comObject.Gain;

        set => _comObject.Gain = value;
    }

    public short GainMin { get; private set; }

    public short GainMax { get; private set; }

    public IReadOnlyList<string> Gains => Connected && UsesGainMode ? _comObject.Gains.AsReadOnly() : [];

    public bool FastReadout
    {
        get => Connected && CanFastReadout && _comObject?.FastReadout is bool fastReadout ? fastReadout : throw new InvalidOperationException("Camera is not connected");
        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (CanFastReadout)
            {
                _comObject.FastReadout = value;
            }
            else
            {
                throw new InvalidOperationException("Fast readout is not supported");
            }
        }
    }

    private IReadOnlyList<string> ReadoutModes
        => Connected && _comObject.ReadoutModes is { } modes ? modes.AsReadOnly() : throw new InvalidOperationException("Camera is not connected");

    public string? ReadoutMode
    {
        get => _comObject.ReadoutMode is { } readoutMode && readoutMode >= 0 && ReadoutModes is { Count: > 0 } modes && readoutMode < modes.Count ? modes[readoutMode] : null;
        set
        {
            int idx;
            if (Connected && value is { Length: > 0 } && (idx = ReadoutModes.IndexOf(value)) is >= 0 and <= short.MaxValue)
            {
                _comObject.ReadoutMode = (short)idx;
            }
        }
    }

    public Float32HxWImageData? ImageData => Connected && _comObject?.ImageArray is int[,] intArray ? Float32HxWImageData.FromWxHImageData(intArray) : null;

    public bool ImageReady => Connected && _comObject is { } obj ? (bool)obj.ImageReady : throw new InvalidOperationException("Camera is not connected");

    public bool IsPulseGuiding => Connected && _comObject is { } obj ? (bool)obj.IsPulseGuiding : throw new InvalidOperationException("Camera is not connected");

    public int MaxADU => Connected && _comObject is { } obj ? (int)obj.MaxADU : throw new InvalidOperationException("Camera is not connected");

    public double FullWellCapacity => _comObject.FullWellCapacity;

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
            obj.PulseGuide((AscomGuideDirection)direction, durationMs);
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

    public CameraState CameraState => Connected ? (CameraState)(int)_comObject.CameraState : CameraState.NotConnected;

    public SensorType SensorType => Connected ? (SensorType)(int)_comObject.SensorType : SensorType.Unknown;

    public int BayerOffsetX => _comObject.BayerOffsetX;

    public int BayerOffsetY => _comObject.BayerOffsetX;

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