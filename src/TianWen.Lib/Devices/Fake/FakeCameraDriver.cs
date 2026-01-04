using System;
using System.Collections.Generic;
using System.Threading;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeCameraDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), ICameraDriver
{
    private readonly Lock _lock = new Lock();

    private Float32HxWImageData? _lastImageData;
    private CameraSettings _cameraSettings;
    private CameraSettings _exposureSettings;
    private ExposureData? _exposureData;
    private short _gain;
    private int _offset;
    private int _cameraState = (int)CameraState.Idle;
    private ITimer? _exposureTimer;

    public bool CanGetCoolerPower { get; } = true;

    public bool CanGetCoolerOn { get; } = true;

    public bool CanSetCoolerOn { get; } = true;

    public bool CanGetCCDTemperature { get; } = true;

    public bool CanSetCCDTemperature { get; } = true;

    public bool CanGetHeatsinkTemperature { get; } = true;

    public bool CanStopExposure { get; } = true;

    public bool CanAbortExposure { get; } = true;

    public bool CanFastReadout { get; } = false;

    public bool CanSetBitDepth { get; } = false;

    public bool CanPulseGuide { get; } = false;

    public bool UsesGainValue { get; } = true;

    public bool UsesGainMode { get; } = false;

    public bool UsesOffsetValue { get; } = true;

    public bool UsesOffsetMode { get; } = false;

    public double PixelSizeX { get; } = 3.7;

    public double PixelSizeY { get; } = 3.7;

    public short MaxBinX { get; } = 2;

    public short MaxBinY { get; } = 2;

    public int BinX
    {
        get
        {
            lock (_lock)
            {
                return _cameraSettings.BinX;
            }
        }

        set
        {
            if (value < 0 || value > MaxBinX)
            {
                throw new ArgumentException($"Bin must be between 0 and {MaxBinX}");
            }

            lock (_lock)
            {
                _cameraSettings = _cameraSettings with { BinX = (byte)value };
            }
        }
    }

    public int BinY { get => BinX; set => BinX = value; }

    public int StartX
    {
        get
        {
            lock (_lock)
            {
                return _cameraSettings.StartX;
            }
        }

        set
        {
            if (value < 0 || value >= PixelSizeX)
            {
                throw new ArgumentException($"Must be between 0 and {CameraXSize}", nameof(value));
            }

            lock (_lock)
            {
                _cameraSettings = _cameraSettings with { StartX = value };
            }
        }
    }

    public int StartY
    {
        get
        {
            lock (_lock)
            {
                return _cameraSettings.StartY;
            }
        }

        set
        {
            if (value < 0 || value >= PixelSizeY)
            {
                throw new ArgumentException($"Must be between 0 and {CameraXSize}", nameof(value));
            }

            lock (_lock)
            {
                _cameraSettings = _cameraSettings with { StartY = value };
            }
        }
    }

    public int NumX
    {
        get
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }

            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (value >= 1 && value * BinX < CameraXSize)
            {
                _cameraSettings = _cameraSettings with { Width = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Width must be between 1 and Camera size (binned)");
            }
        }
    }

    public int NumY
    {
        get
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }

            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if (value >= 1 && value * BinY < CameraYSize)
            {
                _cameraSettings = _cameraSettings with { Height = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Height must be between 1 and Camera size (binned)");
            }
        }
    }

    public int CameraXSize { get; } = 1024;

    public int CameraYSize { get; } = 768;

    public string? ReadoutMode
    {
        get => "Normal";
        set
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }
            else if(value is not "Normal")
            {
                throw new ArgumentException("Readout mode must be \"Normal\"", nameof(value));
            }
        }
    }

    public bool FastReadout
    { 
        get => throw new InvalidOperationException("Fast readout not supported");
        set => throw new InvalidOperationException("Fast readout not supported");
    }

    public Float32HxWImageData? ImageData
    {
        get
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }

            lock (_lock)
            {
                return _lastImageData;
            }
        }
    }

    public bool ImageReady
    {
        get
        {
            if (!Connected)
            {
                throw new InvalidOperationException("Camera is not connected");
            }

            lock (_lock)
            {
                return _lastImageData is not null;
            }
        }
    }

    public bool IsPulseGuiding => Connected ? false : throw new InvalidOperationException("Camera is not connected");

    public bool CoolerOn { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double CoolerPower => throw new NotImplementedException();

    public double SetCCDTemperature { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double HeatSinkTemperature => throw new NotImplementedException();

    public double CCDTemperature => throw new NotImplementedException();

    public BitDepth? BitDepth { get => Devices.BitDepth.Int16; set => throw new InvalidOperationException("Cannot change bit depth"); }

    public short Gain
    {
        get
        {
            lock (_lock)
            {
                return _gain;
            }
        }

        set
        {
            if (value < GainMin || value > GainMax)
            {
                throw new ArgumentException($"Gain must be between {GainMin} and {GainMax}", nameof(value));
            }
        }
    }

    public short GainMin { get; } = 0;

    public short GainMax { get; } = 256;

    public IReadOnlyList<string> Gains { get; } = [];

    public int Offset
    {
        get
        {
            lock (_lock)
            {
                return _offset;
            }
        }

        set
        {
            if (value < OffsetMin || value > OffsetMax)
            {
                throw new ArgumentException($"Offset must be between {OffsetMin} and {OffsetMax}", nameof(value));
            }
        }
    }

    public int OffsetMin { get; } = 0;

    public int OffsetMax { get; } = 100;

    public IReadOnlyList<string> Offsets { get; } = [];

    public double ExposureResolution { get; } = 0.01d;

    public int MaxADU { get; } = 4096;

    public double FullWellCapacity => throw new NotImplementedException();

    public double ElectronsPerADU => throw new NotImplementedException();

    public DateTimeOffset? LastExposureStartTime
    {
        get
        {
            lock (_lock)
            {
                return _exposureData?.StartTime;
            }
        }
    }

    public TimeSpan? LastExposureDuration
    {
        get
        {
            lock (_lock)
            {
                return _exposureData?.ActualDuration;
            }
        }
    }

    public FrameType LastExposureFrameType
    {
        get
        {
            lock (_lock)
            {
                return _exposureData?.FrameType ?? FrameType.None;
            }
        }
    }

    public SensorType SensorType { get; } = SensorType.Monochrome;

    public int BayerOffsetX { get; } = 0;

    public int BayerOffsetY { get; } = 0;

    public CameraState CameraState => (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Error, (int)CameraState.Error);

    public string? Telescope { get; set; }
    public int FocalLength { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Filter Filter { get; set; } = Filter.Unknown;
    public int FocusPosition { get; set; }
    public Target? Target { get; set; }

    public override DeviceType DriverType => DeviceType.Camera;

    public void AbortExposure()
    {
        var previousState = (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Idle, (int)CameraState.Exposing);

        if (previousState is not CameraState.Exposing and not CameraState.Idle)
        {
            throw new InvalidOperationException("Failed to abort exposure");
        }
    }

    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType = FrameType.Light)
    {
        var minDuration = TimeSpan.FromSeconds(ExposureResolution);
        var intentedDuration = duration < minDuration ? minDuration : duration;

        if (frameType is FrameType.None)
        {
            throw new ArgumentException("Fame type cannot be None", nameof(frameType));
        }

        var previousState = (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Exposing, (int)CameraState.Idle);
        if (previousState is CameraState.Idle)
        {
            var startTime = External.TimeProvider.GetUtcNow();

            lock (_lock)
            {
                _exposureSettings = _cameraSettings;
                _exposureData = new ExposureData(startTime, intentedDuration, null, frameType, Gain, Offset);

                var timer = _exposureTimer ??= External.TimeProvider.CreateTimer(_ => StopExposure(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                timer.Change(intentedDuration, Timeout.InfiniteTimeSpan);
            }

            return startTime;
        }
        else
        {
            throw new InvalidOperationException($"Failed to start exposure frame type={frameType} and duration {duration:o} due to camera state being {previousState}");
        }
    }

    public void StopExposure()
    {
        var stopTime = External.TimeProvider.GetUtcNow();

        var previousState = (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Download, (int)CameraState.Exposing);

        if (previousState is CameraState.Exposing)
        {
            if (_exposureData is { } current)
            {
                CameraSettings lastExposureSettings;
                lock (_lock)
                {
                    _exposureData = current with { ActualDuration = stopTime - current.StartTime };
                    _exposureTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    lastExposureSettings = _exposureSettings;
                }
            
                var imageReady = Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Idle, (int)CameraState.Download) is (int)CameraState.Download;

                if (imageReady)
                {
                    var array = new float[
                        lastExposureSettings.Height - lastExposureSettings.StartY, 
                        lastExposureSettings.Width - lastExposureSettings.StartX
                    ];
                    _lastImageData = new Float32HxWImageData(array, current.Offset);
                }
            }

        }
    }

    public void PulseGuide(GuideDirection direction, TimeSpan duration) => throw new InvalidOperationException("Pulse guiding via camera is not supported");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Interlocked.Exchange(ref _exposureTimer, null)?.Dispose();
    }
}