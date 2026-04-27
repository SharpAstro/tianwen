using Microsoft.Extensions.Logging;
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

    internal AscomCameraDriver(AscomDevice device, IServiceProvider sp) : base(device, sp)
    {
        _camera = new AscomDispatchCamera(_dispatchDevice.Dispatch);
    }

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        CanGetCoolerPower = SafeGet(() => _camera.CanGetCoolerPower, false);
        CanSetCCDTemperature = SafeGet(() => _camera.CanSetCCDTemperature, false);
        CanStopExposure = SafeGet(() => _camera.CanStopExposure, false);
        CanAbortExposure = SafeGet(() => _camera.CanAbortExposure, false);
        CanFastReadout = SafeGet(() => _camera.CanFastReadout, false);
        CanPulseGuide = SafeGet(() => _camera.CanPulseGuide, false);

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

        if (SafeGet(() => _camera.InterfaceVersion, (short)0) >= 3)
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

    public double PixelSizeX => SafeGet(() => _camera.PixelSizeX, double.NaN);

    public double PixelSizeY => SafeGet(() => _camera.PixelSizeY, double.NaN);

    public int StartX
    {
        get => SafeGet(() => _camera.StartX, 0);
        set => SafeDo(() => _camera.StartX = value);
    }

    public int StartY
    {
        get => SafeGet(() => _camera.StartY, 0);
        set => SafeDo(() => _camera.StartY = value);
    }

    public int BinX
    {
        get => SafeGet(() => (int)_camera.BinX, 1);
        set => SafeDo(() => _camera.BinX = (short)value);
    }

    public int BinY
    {
        get => SafeGet(() => (int)_camera.BinY, 1);
        set => SafeDo(() => _camera.BinY = (short)value);
    }

    public short MaxBinX => SafeGet(() => _camera.MaxBinX, (short)1);

    public short MaxBinY => SafeGet(() => _camera.MaxBinY, (short)1);

    public int NumX
    {
        get => SafeGet(() => _camera.NumX, 0);
        set => SafeDo(() => _camera.NumX = value);
    }

    public int NumY
    {
        get => SafeGet(() => _camera.NumY, 0);
        set => SafeDo(() => _camera.NumY = value);
    }

    public int CameraXSize => Connected ? SafeGet(() => _camera.CameraXSize, 0) : throw new InvalidOperationException("Camera is not connected");

    public int CameraYSize => Connected ? SafeGet(() => _camera.CameraYSize, 0) : throw new InvalidOperationException("Camera is not connected");

    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _camera.Offset, 0));

    public ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default)
        => SafeValueTask(() => _camera.Offset = value);

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IReadOnlyList<string> Offsets => Connected && UsesOffsetMode ? SafeGet(() => (IReadOnlyList<string>)_camera.Offsets.AsReadOnly(), []) : [];

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _camera.Gain, (short)0));

    public ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
        => SafeValueTask(() => _camera.Gain = value);

    public short GainMin { get; private set; }

    public short GainMax { get; private set; }

    public IReadOnlyList<string> Gains => Connected && UsesGainMode ? SafeGet(() => (IReadOnlyList<string>)_camera.Gains.AsReadOnly(), []) : [];

    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && CanFastReadout ? SafeGet(() => _camera.FastReadout, false) : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Camera is not connected");
        }
        else if (CanFastReadout)
        {
            return SafeValueTask(() => _camera.FastReadout = value);
        }
        else
        {
            throw new InvalidOperationException("Fast readout is not supported");
        }
    }

    private IReadOnlyList<string> ReadoutModes
        => Connected ? SafeGet(() => (IReadOnlyList<string>)_camera.ReadoutModes.AsReadOnly(), []) : throw new InvalidOperationException("Camera is not connected");

    public ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _camera.ReadoutMode, (short)-1) is var readoutMode && readoutMode >= 0 && ReadoutModes is { Count: > 0 } modes && readoutMode < modes.Count ? modes[readoutMode] : (string?)null);

    public ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        int idx;
        if (Connected && value is { Length: > 0 } && (idx = ReadoutModes.IndexOf(value)) is >= 0 and <= short.MaxValue)
        {
            return SafeValueTask(() => _camera.ReadoutMode = (short)idx);
        }
        return ValueTask.CompletedTask;
    }

    public Imaging.Channel? ImageData => Connected ? SafeGet<Imaging.Channel?>(() => Imaging.Channel.FromWxHImageData(_camera.ImageArray), null) : null;
    public void ReleaseImageData() { }

    public int MaxADU => Connected ? SafeGet(() => _camera.MaxADU, 0) : throw new InvalidOperationException("Camera is not connected");

    public double FullWellCapacity => SafeGet(() => _camera.FullWellCapacity, double.NaN);

    public double ElectronsPerADU => Connected ? SafeGet(() => _camera.ElectronsPerADU, double.NaN) : throw new InvalidOperationException("Camera is not connected");

    public DateTimeOffset? LastExposureStartTime { get; private set; }

    public TimeSpan? LastExposureDuration
        => Connected ? SafeGet(() => TimeSpan.FromSeconds(_camera.LastExposureDuration), (TimeSpan?)null) : default;

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

    public double ExposureResolution => Connected ? SafeGet(() => _camera.ExposureResolution, double.NaN) : throw new InvalidOperationException("Camera is not connected");

    public SensorType SensorType => Connected ? SafeGet(() => (SensorType)_camera.SensorType, SensorType.Unknown) : SensorType.Unknown;

    public int BayerOffsetX => SafeGet(() => (int)_camera.BayerOffsetX, 0);

    public int BayerOffsetY => SafeGet(() => (int)_camera.BayerOffsetY, 0);

    // Async-primary members
    public ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => _camera.ImageReady, false) : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => (CameraState)_camera.CameraState, CameraState.Error) : CameraState.NotConnected);

    public ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _camera.CCDTemperature, double.NaN));

    public ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SafeGet(() => _camera.HeatSinkTemperature, double.NaN));

    public ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => _camera.CoolerPower, double.NaN) : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && SafeGet(() => _camera.CoolerOn, false));

    public ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (Connected)
        {
            return SafeValueTask(() => _camera.CoolerOn = value);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && CanSetCCDTemperature ? SafeGet(() => _camera.SetCCDTemperature, double.NaN) : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        if (Connected && CanSetCCDTemperature)
        {
            return SafeValueTask(() => _camera.SetCCDTemperature = value);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? SafeGet(() => _camera.IsPulseGuiding, false) : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        try
        {
            _camera.StartExposure(duration.TotalSeconds, frameType.NeedsOpenShutter);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ASCOM {DeviceId} StartExposure threw {Type}: {Msg}",
                _device.DeviceId, ex.GetType().Name, ex.Message);
            return ValueTask.FromException<DateTimeOffset>(ex);
        }
        var startTime = TimeProvider.System.GetLocalNow();
        LastExposureStartTime = startTime;
        LastExposureFrameType = frameType;
        return ValueTask.FromResult(startTime);
    }

    public ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
    {
        if (CanStopExposure && Connected)
        {
            return SafeValueTask(() => _camera.StopExposure());
        }
        else
        {
            throw new InvalidOperationException("Failed to stop exposure");
        }
    }

    public ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        if (CanAbortExposure && Connected)
        {
            return SafeValueTask(() => _camera.AbortExposure());
        }
        else
        {
            throw new InvalidOperationException("Failed to abort exposure");
        }
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var durationMs = (int)duration.Round(TimeSpanRoundingType.Millisecond).TotalMilliseconds;

        if (Connected)
        {
            return SafeValueTask(() => _camera.PulseGuide((int)direction, durationMs));
        }
        else
        {
            throw new InvalidOperationException("Camera is not connected, cannot pulse guide");
        }
    }

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int? Aperture { get; set; }

    public int FocusPosition { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public Target? Target { get; set; }
    #endregion
}
