using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeCameraDriver : FakeDeviceDriverBase, ICameraDriver
{
    public FakeCameraDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : base(fakeDevice, serviceProvider)
    {
        var preset = GetPreset(fakeDevice);
        PixelSizeX = preset.PixelSize;
        PixelSizeY = preset.PixelSize;
        MaxBinX = preset.MaxBin;
        MaxBinY = preset.MaxBin;
        CameraXSize = preset.Width;
        CameraYSize = preset.Height;
        GainMin = preset.GainMin;
        GainMax = preset.GainMax;
        MaxADU = preset.MaxADU;
        SensorType = preset.SensorType;
    }

    private readonly Lock _lock = new Lock();
    private readonly Random _frameRng = new Random(42);

    protected override void OnConnected()
    {
        // Initialize sensor readout area to full frame on first connect.
        // BinX must be set first — NumX/NumY setters validate against binned size.
        lock (_lock)
        {
            if (_cameraSettings.BinX <= 0)
            {
                _cameraSettings = _cameraSettings with { BinX = 1 };
            }
            if (_cameraSettings.Width <= 0)
            {
                _cameraSettings = _cameraSettings with { Width = (ushort)CameraXSize };
            }
            if (_cameraSettings.Height <= 0)
            {
                _cameraSettings = _cameraSettings with { Height = (ushort)CameraYSize };
            }
        }

        // Read PE simulation parameters from device URI query params
        if (_fakeDevice.Query.QueryValue(DeviceQueryKey.PePeriodSeconds) is { } pePeriod
            && double.TryParse(pePeriod, System.Globalization.CultureInfo.InvariantCulture, out var period)
            && period > 0)
        {
            PePeriodSeconds = period;
        }
        if (_fakeDevice.Query.QueryValue(DeviceQueryKey.PePeakTopeakArcsec) is { } peAmplitude
            && double.TryParse(peAmplitude, System.Globalization.CultureInfo.InvariantCulture, out var amplitude)
            && amplitude >= 0)
        {
            PePeakTopeakArcsec = amplitude;
        }
    }

    private Imaging.Channel? _lastImageData;
    private ChannelBuffer? _channelBuffer;

    // Recycled buffers returned by consumers via ChannelBuffer.onRelease.
    // Camera picks from here before allocating fresh in Render(dest:).
    private readonly System.Collections.Concurrent.ConcurrentBag<float[,]> _freeBuffers = new();
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

    public bool CanPulseGuide { get; } = true;

    // Accumulated star position (PE drift + ST-4 corrections share the same integrator)
    private double _starPositionX;
    private double _starPositionY;
    private long _peStartTicks;  // phase reference (never updated)
    private long _lastPeTicks;   // last integration timestamp

    /// <summary>Periodic error period in seconds (typical worm gear ~10 minutes).</summary>
    internal double PePeriodSeconds { get; set; } = 600.0;

    /// <summary>
    /// Periodic error peak-to-peak amplitude in arcseconds (typical ~20").
    /// </summary>
    internal double PePeakTopeakArcsec { get; set; } = 20.0;

    /// <summary>
    /// Guide rate in pixels per second, derived from 0.5x sidereal rate and the camera's pixel scale.
    /// Falls back to 2 px/s when FocalLength is not configured (typical for ~130mm guide scope + small pixels).
    /// </summary>
    internal double GuideRatePixelsPerSecond
    {
        get
        {
            if (FocalLength <= 0)
            {
                return 2.0; // sensible default for guide cameras
            }
            const double halfSiderealArcsecPerSec = 15.041 * 0.5;
            var pixelScaleArcsec = Astrometry.CoordinateUtils.PixelScaleArcsec(PixelSizeX, FocalLength);
            return halfSiderealArcsecPerSec / pixelScaleArcsec;
        }
    }

    /// <summary>
    /// PE drift rate amplitude in pixels/second.
    /// Derived from peak-to-peak arcsec → amplitude pixels → rate via ω.
    /// </summary>
    private double PeRateAmplitude
    {
        get
        {
            var pixelScaleArcsec = FocalLength > 0 ? Astrometry.CoordinateUtils.PixelScaleArcsec(PixelSizeX, FocalLength) : 3.8; // ~130mm + 2.4µm default
            var amplitudePixels = PePeakTopeakArcsec / 2.0 / pixelScaleArcsec;
            return 2.0 * Math.PI / PePeriodSeconds * amplitudePixels;
        }
    }

    /// <summary>
    /// Advances star position by integrating PE drift since the last call.
    /// Called each frame before rendering.
    /// </summary>
    private void IntegratePeDrift()
    {
        if (PePeakTopeakArcsec <= 0)
        {
            return;
        }

        var now = TimeProvider.GetTimestamp();
        if (_peStartTicks == 0)
        {
            _peStartTicks = now;
            _lastPeTicks = now;
            return;
        }

        var dt = TimeProvider.GetElapsedTime(_lastPeTicks).TotalSeconds;
        _lastPeTicks = now;

        // Phase from session start (stable reference, not affected by dt update)
        var phase = TimeProvider.GetElapsedTime(_peStartTicks).TotalSeconds;
        var driftRate = PeRateAmplitude * Math.Sin(2.0 * Math.PI * phase / PePeriodSeconds);
        _starPositionX += driftRate * dt;
    }

    public bool UsesGainValue { get; } = true;

    public bool UsesGainMode { get; } = false;

    public bool UsesOffsetValue { get; } = true;

    public bool UsesOffsetMode { get; } = false;

    public double PixelSizeX { get; }

    public double PixelSizeY { get; }

    public short MaxBinX { get; }

    public short MaxBinY { get; }

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

            return _cameraSettings.Width;
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

    public int CameraXSize { get; }

    public int CameraYSize { get; }

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

    public Imaging.Channel? ImageData
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

    Imaging.ChannelBuffer? ICameraDriver.ChannelBuffer => _channelBuffer;

    public void ReleaseImageData()
    {
        lock (_lock)
        {
            // Clear channel buffer — ownership was transferred to the Image in GetImageAsync.
            // Keep _lastImageData so GetImageReadyAsync still returns true until next StartExposureAsync.
            _channelBuffer = null;
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

    public short GainMin { get; }

    public short GainMax { get; }

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

    public int MaxADU { get; }

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

    public SensorType SensorType { get; }

    public int BayerOffsetX { get; } = 0;

    public int BayerOffsetY { get; } = 0;

    public string? Telescope { get; set; }
    public int FocalLength { get; set; }
    public int? Aperture { get; set; }
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

    /// <summary>
    /// Simulated cloud coverage (0.0 = clear, 1.0 = overcast). Applied to rendered frames
    /// as streaky attenuation + diffuse glow. Set dynamically during a session to simulate
    /// clouds rolling in/out.
    /// </summary>
    public double CloudCoverage { get; set; }

    /// <summary>
    /// Test seam: when &gt; 0, the next <see cref="StartExposureAsync"/> call throws
    /// <see cref="System.IO.IOException"/> (classified as transient by
    /// <c>ResilientCall</c>) and decrements the counter. Use to script
    /// "first attempt fails, second succeeds" scenarios for verifying
    /// <c>TakeScoutFrameAsync</c>'s Layer 2 retry recovers correctly.
    /// </summary>
    internal int TransientStartExposureFailures;

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
        // Test seam: simulate a transient driver fault (USB bump, COM glitch).
        // ResilientCall classifies IOException as transient, so it triggers the
        // appropriate retry/reconnect path depending on the call's preset.
        if (Interlocked.Decrement(ref TransientStartExposureFailures) >= 0)
        {
            throw new System.IO.IOException("Simulated transient camera fault (test seam).");
        }
        // Decrement went below 0 — clamp to 0 so steady state stays at 0.
        Interlocked.CompareExchange(ref TransientStartExposureFailures, 0, -1);

        var minDuration = TimeSpan.FromSeconds(ExposureResolution);
        var intentedDuration = duration < minDuration ? minDuration : duration;

        if (frameType is FrameType.None)
        {
            throw new ArgumentException("Fame type cannot be None", nameof(frameType));
        }

        var previousState = (CameraState)Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Exposing, (int)CameraState.Idle);
        if (previousState is CameraState.Idle)
        {
            var startTime = TimeProvider.GetUtcNow();

            lock (_lock)
            {
                _lastImageData = null; // Clear previous image so GetImageReadyAsync returns false
                _exposureSettings = _cameraSettings;
                _exposureData = new ExposureData(startTime, intentedDuration, null, frameType, _gain, _offset);

                var timer = _exposureTimer ??= TimeProvider.CreateTimer(_ => StopExposureCore(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                timer.Change(intentedDuration, Timeout.InfiniteTimeSpan);
            }

            return ValueTask.FromResult(startTime);
        }
        else
        {
            throw new InvalidOperationException($"Failed to start exposure frame type={frameType} and duration {duration} due to camera state being {previousState}");
        }
    }

    private void StopExposureCore()
    {
        var stopTime = TimeProvider.GetUtcNow();

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

                    // Try to reuse a recycled buffer from the free pool
                    float[,]? dest = null;
                    while (_freeBuffers.TryTake(out var free))
                    {
                        if (free.GetLength(0) == imgHeight && free.GetLength(1) == imgWidth)
                        {
                            dest = free;
                            break;
                        }
                        // Wrong size — drop it
                    }

                    float[,] array;
                    if (TrueBestFocus is { } bestFocus)
                    {
                        var defocus = Math.Abs(FocusPosition - bestFocus);
                        var exposureSec = current.IntendedDuration.TotalSeconds;

                        if (CelestialObjectDB is { } db && Target is { } target && FocalLength > 0)
                        {
                            // Limiting magnitude scales as collecting area (D^2) and
                            // exposure time. Standard photon-counting form, calibrated
                            // against amateur CMOS observations:
                            //   m_lim = 2 + 5*log10(D_mm) + 2.5*log10(t_sec)
                            // (50mm at 1s -> ~mag 10.5; 200mm at 10s -> ~mag 16.)
                            // Aperture is denormalised onto the camera in Session.Lifecycle
                            // and the polar-alignment AppSignalHandler. Without it, fall
                            // back to the legacy exposure-only formula so unit tests that
                            // construct FakeCameraDriver standalone still get sensible
                            // (if conservative) star counts.
                            // Cap at 15 to bound frame-render time and stay within the
                            // catalog's reliable density region.
                            var safeExposure = Math.Max(exposureSec, 0.1);
                            var magCutoff = Aperture is int apertureMm and > 0
                                ? Math.Min(15.0, 2.0 + 5.0 * Math.Log10(apertureMm) + 2.5 * Math.Log10(safeExposure))
                                : Math.Min(12.0, 7.0 + 2.5 * Math.Log10(safeExposure));
                            var stars = SyntheticStarFieldRenderer.ProjectCatalogStars(
                                target.RA, target.Dec, FocalLength, PixelSizeX, imgWidth, imgHeight, db, magCutoff);
                            var cloudSeed = _frameRng.Next();
                            var starSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stars);
                            array = SensorType is Imaging.SensorType.RGGB
                                ? SyntheticStarFieldRenderer.RenderBayer(imgWidth, imgHeight, defocus, starSpan,
                                    offsetX: TotalStarOffset.X, offsetY: TotalStarOffset.Y,
                                    exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(), dest: dest)
                                : SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocus,
                                    stars: starSpan, offsetX: TotalStarOffset.X, offsetY: TotalStarOffset.Y,
                                    exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                                    cloudCoverage: CloudCoverage, cloudSeed: cloudSeed, dest: dest);
                        }
                        else
                        {
                            var cloudSeed = _frameRng.Next();
                            // No catalog — random stars, can't do meaningful Bayer colors
                            array = SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocus,
                                offsetX: TotalStarOffset.X, offsetY: TotalStarOffset.Y,
                                exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                                cloudCoverage: CloudCoverage, cloudSeed: cloudSeed, dest: dest);
                        }
                    }
                    else
                    {
                        // No TrueBestFocus explicitly set — if FocusPosition was never set
                        // by a focuser (e.g. guide camera with no dedicated focuser), render
                        // in perfect focus. Otherwise use default best focus of 1000.
                        var defocus = FocusPosition <= 0 ? 0 : Math.Abs(FocusPosition - 1000);
                        var exposureSec = current.IntendedDuration.TotalSeconds;
                        var cloudSeed = _frameRng.Next();
                        array = SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocusSteps: defocus,
                            offsetX: TotalStarOffset.X, offsetY: TotalStarOffset.Y,
                            exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                            cloudCoverage: CloudCoverage, cloudSeed: cloudSeed, dest: dest);
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

                    _channelBuffer = new ChannelBuffer(array, onRelease: recycled => _freeBuffers.Add(recycled));
                    _lastImageData = new Imaging.Channel(array, Filter, dataMin, dataMax, 0);
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
    {
        var pixels = GuideRatePixelsPerSecond * duration.TotalSeconds;
        // ST-4 corrections modify the same accumulator as PE drift.
        // West = speed up RA tracking = stars shift -X (counteracts +X drift)
        switch (direction)
        {
            case GuideDirection.North: _starPositionY -= pixels; break;
            case GuideDirection.South: _starPositionY += pixels; break;
            case GuideDirection.West:  _starPositionX -= pixels; break;
            case GuideDirection.East:  _starPositionX += pixels; break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Current star offset: integrated PE drift + accumulated ST-4 corrections.
    /// Both operate on the same position — corrections push back against drift.
    /// </summary>
    private (double X, double Y) TotalStarOffset
    {
        get
        {
            IntegratePeDrift();
            return (_starPositionX, _starPositionY);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Interlocked.Exchange(ref _exposureTimer, null)?.Dispose();
    }

    /// <summary>
    /// Sensor preset for a fake camera. Each fake device ID maps to a real sensor model.
    /// </summary>
    internal readonly record struct SensorPreset(
        string SensorName,
        int Width,
        int Height,
        double PixelSize,
        SensorType SensorType,
        short MaxBin,
        short GainMin,
        short GainMax,
        int MaxADU);

    /// <summary>
    /// Sensor presets indexed by fake device ID (mod table length).
    /// </summary>
    /// <summary>Guide camera preset (IMX178M). Not in the main list — used only for FakeGuideCam.</summary>
    internal static readonly SensorPreset GuideCameraPreset =
        new SensorPreset("IMX178M", 3096, 2080, 2.4, SensorType.Monochrome, 4, 0, 570, 16383);

    /// <summary>
    /// Imaging camera presets indexed by (deviceId - 1) mod length.
    /// Alternates color/mono: odd IDs = color, even IDs = mono.
    /// </summary>
    private static readonly SensorPreset[] Presets =
    [
        // ID 1: Sony IMX294C — large color, deep sky workhorse
        new SensorPreset("IMX294C", 4144, 2822, 4.63, SensorType.RGGB, 4, 0, 570, 65535),
        // ID 2: Sony IMX533M — square mono, narrowband
        new SensorPreset("IMX533M", 3008, 3008, 3.76, SensorType.Monochrome, 4, 0, 460, 65535),
        // ID 3: Sony IMX571C — APS-C color, wide-field
        new SensorPreset("IMX571C", 6248, 4176, 3.76, SensorType.RGGB, 4, 0, 100, 65535),
        // ID 4: Sony IMX455M — full-frame mono, premium
        new SensorPreset("IMX455M", 9576, 6388, 3.76, SensorType.Monochrome, 4, 0, 100, 65535),
        // ID 5: Sony IMX585C — color, fast planetary/EAA
        new SensorPreset("IMX585C", 3856, 2180, 2.9, SensorType.RGGB, 4, 0, 570, 65535),
        // ID 6: Sony IMX411M — medium-format mono
        new SensorPreset("IMX411M", 14208, 10656, 3.76, SensorType.Monochrome, 4, 0, 100, 65535),
        // ID 7: Sony IMX410C — full-frame color
        new SensorPreset("IMX410C", 6072, 4042, 3.76, SensorType.RGGB, 4, 0, 100, 65535),
        // ID 8: Sony IMX464M — compact mono, planetary
        new SensorPreset("IMX464M", 2712, 1538, 2.9, SensorType.Monochrome, 4, 0, 570, 65535),
        // ID 9: Sony IMX678C — small color, high QE
        new SensorPreset("IMX678C", 3856, 2180, 2.0, SensorType.RGGB, 4, 0, 570, 65535),
    ];

    private static SensorPreset GetPreset(FakeDevice device)
    {
        // Guide camera uses a dedicated preset (IMX178M)
        if (device.DeviceUri.AbsolutePath.Contains("GuideCam", StringComparison.OrdinalIgnoreCase))
        {
            return GuideCameraPreset;
        }
        return GetPresetForId(ExtractId(device.DeviceUri));
    }

    /// <summary>
    /// Returns the sensor preset for a given device ID (1-based, maps to 0-based index mod preset count).
    /// </summary>
    internal static SensorPreset GetPresetForId(int id) => Presets[(id - 1) % Presets.Length];

    /// <summary>
    /// Extracts the numeric ID from a fake device URI path (e.g. "FakeCamera1" → 1).
    /// </summary>
    internal static int ExtractId(Uri deviceUri)
    {
        var path = deviceUri.AbsolutePath.TrimStart('/');
        var id = 0;
        for (var i = path.Length - 1; i >= 0 && char.IsAsciiDigit(path[i]); i--)
        {
            id += (path[i] - '0') * (int)Math.Pow(10, path.Length - 1 - i);
        }
        return id;
    }
}
