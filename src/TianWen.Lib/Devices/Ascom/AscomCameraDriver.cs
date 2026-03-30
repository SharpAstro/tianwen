using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices.Ascom.ComInterop;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Ascom;

[SupportedOSPlatform("windows")]
internal class AscomCameraDriver : AscomDeviceDriverBase, ICameraDriver
{
    private readonly AscomDispatchCamera _camera;

    internal AscomCameraDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        _camera = new AscomDispatchCamera(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        CanGetCoolerPower = _camera.CanGetCoolerPower;
        CanSetCCDTemperature = _camera.CanSetCCDTemperature;
        CanStopExposure = _camera.CanStopExposure;
        CanAbortExposure = _camera.CanAbortExposure;
        CanFastReadout = _camera.CanFastReadout;
        CanPulseGuide = _camera.CanPulseGuide;

        try
        {
            _ = _camera.CoolerOn;
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
            CanGetHeatsinkTemperature = !double.IsNaN(_camera.HeatSinkTemperature);
        }
        catch
        {
            CanGetHeatsinkTemperature = false;
        }

        try
        {
            CanGetCCDTemperature = !double.IsNaN(_camera.CCDTemperature);
        }
        catch
        {
            CanGetCCDTemperature = false;
        }

        if (_camera.InterfaceVersion >= 3)
        {
            try
            {
                _ = _camera.Offset;
                var min = _camera.OffsetMin;
                var max = _camera.OffsetMax;
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
                    _ = _camera.Offset;
                    _ = _camera.Offsets;
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
            _ = _camera.Gain;
            var min = _camera.GainMin;
            var max = _camera.GainMax;
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
                _ = _camera.Gain;
                _ = _camera.Gains;
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

    public double PixelSizeX => _camera.PixelSizeX;

    public double PixelSizeY => _camera.PixelSizeY;

    public int StartX
    {
        get => _camera.StartX;
        set => _camera.StartX = value;
    }

    public int StartY
    {
        get => _camera.StartY;
        set => _camera.StartY = value;
    }

    public int BinX
    {
        get => _camera.BinX;
        set => _camera.BinX = (short)value;
    }

    public int BinY
    {
        get => _camera.BinY;
        set => _camera.BinY = (short)value;
    }

    public short MaxBinX => _camera.MaxBinX;

    public short MaxBinY => _camera.MaxBinY;

    public int NumX
    {
        get => _camera.NumX;
        set => _camera.NumX = value;
    }

    public int NumY
    {
        get => _camera.NumY;
        set => _camera.NumY = value;
    }

    public int CameraXSize => Connected ? _camera.CameraXSize : throw new InvalidOperationException("Camera is not connected");

    public int CameraYSize => Connected ? _camera.CameraYSize : throw new InvalidOperationException("Camera is not connected");

    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera.Offset);

    public ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default)
    {
        _camera.Offset = value;
        return ValueTask.CompletedTask;
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IReadOnlyList<string> Offsets => Connected && UsesOffsetMode ? _camera.Offsets.AsReadOnly() : [];

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera.Gain);

    public ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
    {
        _camera.Gain = value;
        return ValueTask.CompletedTask;
    }

    public short GainMin { get; private set; }

    public short GainMax { get; private set; }

    public IReadOnlyList<string> Gains => Connected && UsesGainMode ? _camera.Gains.AsReadOnly() : [];

    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && CanFastReadout ? _camera.FastReadout : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Camera is not connected");
        }
        else if (CanFastReadout)
        {
            _camera.FastReadout = value;
        }
        else
        {
            throw new InvalidOperationException("Fast readout is not supported");
        }
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<string> ReadoutModes
        => Connected ? _camera.ReadoutModes.AsReadOnly() : throw new InvalidOperationException("Camera is not connected");

    public ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera.ReadoutMode is var readoutMode && readoutMode >= 0 && ReadoutModes is { Count: > 0 } modes && readoutMode < modes.Count ? modes[readoutMode] : (string?)null);

    public ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        int idx;
        if (Connected && value is { Length: > 0 } && (idx = ReadoutModes.IndexOf(value)) is >= 0 and <= short.MaxValue)
        {
            _camera.ReadoutMode = (short)idx;
        }
        return ValueTask.CompletedTask;
    }

    public Imaging.Channel? ImageData => Connected ? Imaging.Channel.FromWxHImageData(_camera.ImageArray) : null;
public void ReleaseImageData() { }

    public int MaxADU => Connected ? _camera.MaxADU : throw new InvalidOperationException("Camera is not connected");

    public double FullWellCapacity => _camera.FullWellCapacity;

    public double ElectronsPerADU => Connected ? _camera.ElectronsPerADU : throw new InvalidOperationException("Camera is not connected");

    public DateTimeOffset? LastExposureStartTime { get; private set; }

    public TimeSpan? LastExposureDuration
        => Connected ? TimeSpan.FromSeconds(_camera.LastExposureDuration) : default;

    public FrameType LastExposureFrameType { get; internal set; }

    public ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default)
    {
        var maxADU = MaxADU;
        if (maxADU is <= 0 || double.IsNaN(FullWellCapacity))
        {
            return ValueTask.FromResult<BitDepth?>(null);
        }

        if (maxADU == byte.MaxValue && MaxADU < FullWellCapacity && Name.Contains("QHYCCD", StringComparison.OrdinalIgnoreCase))
        {
            maxADU = (int)FullWellCapacity;
        }

        int log2 = (int)MathF.Ceiling(MathF.Log(maxADU) / MathF.Log(2.0f));
        var bytesPerPixel = (log2 + 7) / 8;
        int bitDepth = bytesPerPixel * 8;

        return ValueTask.FromResult(BitDepthEx.FromValue(bitDepth));
    }

    public ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Setting bit depth is not supported!");

    public double ExposureResolution => Connected ? _camera.ExposureResolution : throw new InvalidOperationException("Camera is not connected");

    public SensorType SensorType => Connected ? (SensorType)_camera.SensorType : SensorType.Unknown;

    public int BayerOffsetX => _camera.BayerOffsetX;

    public int BayerOffsetY => _camera.BayerOffsetY;

    // Async-primary members
    public ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? _camera.ImageReady : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? (CameraState)_camera.CameraState : CameraState.NotConnected);

    public ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera.CCDTemperature);

    public ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera.HeatSinkTemperature);

    public ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? _camera.CoolerPower : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && _camera.CoolerOn);

    public ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (Connected)
        {
            _camera.CoolerOn = value;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && CanSetCCDTemperature ? _camera.SetCCDTemperature : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        if (Connected && CanSetCCDTemperature)
        {
            _camera.SetCCDTemperature = value;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? _camera.IsPulseGuiding : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        _camera.StartExposure(duration.TotalSeconds, frameType.NeedsOpenShutter);
        var startTime = External.TimeProvider.GetLocalNow();
        LastExposureStartTime = startTime;
        LastExposureFrameType = frameType;
        return ValueTask.FromResult(startTime);
    }

    public ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
    {
        if (CanStopExposure && Connected)
        {
            _camera.StopExposure();
        }
        else
        {
            throw new InvalidOperationException("Failed to stop exposure");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        if (CanAbortExposure && Connected)
        {
            _camera.AbortExposure();
        }
        else
        {
            throw new InvalidOperationException("Failed to abort exposure");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var durationMs = (int)duration.Round(TimeSpanRoundingType.Millisecond).TotalMilliseconds;

        if (Connected)
        {
            _camera.PulseGuide((int)direction, durationMs);
        }
        else
        {
            throw new InvalidOperationException("Camera is not connected, cannot pulse guide");
        }
        return ValueTask.CompletedTask;
    }

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
