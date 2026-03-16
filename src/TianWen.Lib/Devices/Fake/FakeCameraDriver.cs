using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeCameraDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), ICameraDriver
{
    private readonly Lock _lock = new Lock();
    private readonly Random _frameRng = new Random(42);

    private Float32HxWImageData? _lastImageData;
    private CameraSettings _cameraSettings;
    private CameraSettings _exposureSettings;
    private ExposureData? _exposureData;
    private short _gain;
    private int _offset;
    private int _cameraState = (int)CameraState.Idle;
    private ITimer? _exposureTimer;

    /// <summary>
    /// The true best focus position from the focuser. Set by test setup or Session
    /// to enable synthetic star field rendering with defocus-dependent PSF.
    /// When null, produces empty images (legacy behavior).
    /// </summary>
    public int? TrueBestFocus { get; set; }

    // Cooling simulation
    private double _ccdTemperature = 20.0;
    private double _heatsinkTemperature = 20.0;
    private double _setpointTemperature = 20.0;
    private bool _coolerOn;
    private double _coolerPower;

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

    public ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>("Normal");

    public ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Camera is not connected");
        }
        else if(value is not "Normal")
        {
            throw new ArgumentException("Readout mode must be \"Normal\"", nameof(value));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Fast readout not supported");

    public ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Fast readout not supported");

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

    public ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<BitDepth?>(Imaging.BitDepth.Int16);

    public ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Cannot change bit depth");

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_gain);
        }
    }

    public ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
    {
        if (value < GainMin || value > GainMax)
        {
            throw new ArgumentException($"Gain must be between {GainMin} and {GainMax}", nameof(value));
        }

        lock (_lock)
        {
            _gain = value;
        }
        return ValueTask.CompletedTask;
    }

    public short GainMin { get; } = 0;

    public short GainMax { get; } = 256;

    public IReadOnlyList<string> Gains { get; } = [];

    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_offset);
        }
    }

    public ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default)
    {
        if (value < OffsetMin || value > OffsetMax)
        {
            throw new ArgumentException($"Offset must be between {OffsetMin} and {OffsetMax}", nameof(value));
        }

        lock (_lock)
        {
            _offset = value;
        }
        return ValueTask.CompletedTask;
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

    public string? Telescope { get; set; }
    public int FocalLength { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Filter Filter { get; set; } = Filter.Unknown;
    public int FocusPosition { get; set; }
    public Target? Target { get; set; }

    /// <summary>
    /// When set together with <see cref="Target"/> and <see cref="TrueBestFocus"/>,
    /// renders catalog stars from Tycho-2 projected onto the sensor via TAN projection
    /// instead of generating random star positions.
    /// </summary>
    public ICelestialObjectDB? CelestialObjectDB { get; set; }

    // Async-primary members
    public ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Camera is not connected");
        }

        lock (_lock)
        {
            return ValueTask.FromResult(_lastImageData is not null);
        }
    }

    public ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult((CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Error, (int)CameraState.Error));

    public ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default)
    {
        if (_coolerOn)
        {
            // Move 1°C toward setpoint per call when cooler is on
            var delta = _setpointTemperature - _ccdTemperature;
            if (Math.Abs(delta) > 0.1)
            {
                _ccdTemperature += Math.Sign(delta) * Math.Min(1.0, Math.Abs(delta));
            }
            else
            {
                _ccdTemperature = _setpointTemperature;
            }

            // Cooler power proportional to temperature difference from heatsink
            _coolerPower = Math.Clamp((_heatsinkTemperature - _ccdTemperature) / 40.0 * 100.0, 0, 100);
        }

        return ValueTask.FromResult(_ccdTemperature);
    }

    public ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_heatsinkTemperature);

    public ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_coolerPower);

    public ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_coolerOn);

    public ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default)
    {
        _coolerOn = value;
        if (!value)
        {
            _coolerPower = 0;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_setpointTemperature);

    public ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        _setpointTemperature = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? false : throw new InvalidOperationException("Camera is not connected"));

    public ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
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
                _lastImageData = null; // Clear previous image so GetImageReadyAsync returns false
                _exposureSettings = _cameraSettings;
                _exposureData = new ExposureData(startTime, intentedDuration, null, frameType, _gain, _offset);

                var timer = _exposureTimer ??= External.TimeProvider.CreateTimer(_ => StopExposureCore(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                timer.Change(intentedDuration, Timeout.InfiniteTimeSpan);
            }

            return ValueTask.FromResult(startTime);
        }
        else
        {
            throw new InvalidOperationException($"Failed to start exposure frame type={frameType} and duration {duration:o} due to camera state being {previousState}");
        }
    }

    private void StopExposureCore()
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
                    var imgHeight = lastExposureSettings.Height - lastExposureSettings.StartY;
                    var imgWidth = lastExposureSettings.Width - lastExposureSettings.StartX;

                    float[,] array;
                    if (TrueBestFocus is { } bestFocus)
                    {
                        var defocus = Math.Abs(FocusPosition - bestFocus);
                        var exposureSec = current.IntendedDuration.TotalSeconds;

                        if (CelestialObjectDB is { } db && Target is { } target && FocalLength > 0)
                        {
                            // Magnitude cutoff: brighter limit for shorter exposures
                            // ~mag 8 at 1s, ~mag 10 at 10s, ~mag 12 at 120s
                            var magCutoff = Math.Min(12.0, 7.0 + 2.5 * Math.Log10(Math.Max(exposureSec, 0.1)));
                            var stars = SyntheticStarFieldRenderer.ProjectCatalogStars(
                                target.RA, target.Dec, FocalLength, PixelSizeX, imgWidth, imgHeight, db, magCutoff);
                            array = SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocus,
                                stars: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stars),
                                exposureSeconds: exposureSec, noiseSeed: _frameRng.Next());
                        }
                        else
                        {
                            array = SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocus,
                                exposureSeconds: exposureSec, noiseSeed: _frameRng.Next());
                        }
                    }
                    else
                    {
                        array = new float[imgHeight, imgWidth];
                    }

                    // Compute actual min/max of the rendered data
                    var dataMax = 0f;
                    var dataMin = float.MaxValue;
                    for (var y = 0; y < array.GetLength(0); y++)
                    {
                        for (var x = 0; x < array.GetLength(1); x++)
                        {
                            var val = array[y, x];
                            if (val > dataMax) dataMax = val;
                            if (val < dataMin) dataMin = val;
                        }
                    }

                    _lastImageData = new Float32HxWImageData([array], dataMax, dataMin);
                }
            }

        }
    }

    public ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
    {
        StopExposureCore();
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        var previousState = (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Idle, (int)CameraState.Exposing);

        if (previousState is not CameraState.Exposing and not CameraState.Idle)
        {
            throw new InvalidOperationException("Failed to abort exposure");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Pulse guiding via camera is not supported");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Interlocked.Exchange(ref _exposureTimer, null)?.Dispose();
    }
}
