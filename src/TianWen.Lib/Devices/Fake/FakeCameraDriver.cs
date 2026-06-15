using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
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
    /// Ground-truth focuser step position used by the synthetic star renderer
    /// to compute defocus (and therefore PSF FWHM). Defaults to 1000 to match
    /// <see cref="FakeFocuserDriver"/>'s default base best focus, so a vanilla
    /// fake-camera + fake-focuser pair lands on sharp frames once the focuser
    /// reaches step 1000 (initial position is 980 -- jog +20 or use the goto
    /// input). AutoFocus tests override this explicitly to verify the V-curve
    /// fit converges back from a deliberately-drifted FocusPosition.
    /// </summary>
    public int TrueBestFocus { get; set; } = 1000;

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

    // ── Part 2: guide-camera mount coupling ──────────────────────────────────
    // The guide star drifts on the sensor by the coupled mount's misalignment
    // pointing change (the fake SkyWatcher mount drifts mostly in Dec while
    // tracking about a tilted polar axis). Guide corrections counter that drift
    // by MOVING THE MOUNT: pulses routed to the mount directly
    // (pulseGuideSource=Mount/Auto) or via this camera's ST-4 port
    // (pulseGuideSource=Camera, forwarded as if a guide cable were wired to the
    // mount's autoguide port) change the encoders and hence the pointing the
    // next exposure snapshots — the loop closes through the mount with no
    // session awareness; the camera self-resolves the mount from the device
    // hub, mirroring how it self-resolves the catalog DB. The in-camera
    // _starPositionX/Y integrator only models sensor-visible periodic error
    // (worm PE never appears in encoder/pointing reads) plus, when no mount is
    // coupled at all, the legacy self-contained ST-4 shift for standalone tests.
    // Cached pointing is read in the (async) StartExposureAsync and consumed by
    // the (sync) render path; both are guarded by _lock.
    private IMountDriver? _coupledMount;
    private Transform? _coupledMountTransform; // reused SOFA transform for native -> J2000 (built once, time refreshed per exposure)
    private bool _mountRefCaptured;     // reference (zero-drift) pointing captured?
    private double _mountRefRa;         // believed RA at first guide exposure (J2000 hours)
    private double _mountRefDec;        // believed Dec (J2000 degrees)
    private double _mountCachedRa;      // mount RA snapshot from the last StartExposureAsync (J2000 hours)
    private double _mountCachedDec;     // mount Dec snapshot (J2000 degrees)
    private bool _mountPointingValid;   // a snapshot has been taken at least once

    // Main-camera-only: (true - believed) J2000 pointing delta snapshot taken per
    // exposure when the coupled mount models hidden alignment errors
    // (IFakeTruePointingSource). The stamped Target is a believed-pointing quantity
    // (session/preview stamp it from the mount's public reads), but the sensor sees
    // the TRUE sky - the render path shifts the catalog projection centre by this
    // delta so a plate solve of a main frame reveals the hidden cone/polar error
    // (which is exactly what the centering loop syncs away). Stays (0, 0) for guide
    // cameras (their projection centre is the live true pointing already), real
    // mounts, or no coupled mount. Guarded by _lock like the other snapshots.
    private double _trueMinusBelievedRa;  // J2000 hours
    private double _trueMinusBelievedDec; // J2000 degrees

    /// <summary>
    /// True when this fake camera is the rig's guide camera (its URI names it so).
    /// Reuses the same path convention <see cref="GetPreset"/> keys off to pick the
    /// IMX178M preset, so "the guide cam" is identified consistently. Only the guide
    /// camera couples its rendered star field to the mount's drift; main imaging
    /// cameras render at their stamped <see cref="Target"/> and must not double-count
    /// the drift (the session's centering loop plate-solves and re-syncs them).
    /// </summary>
    private bool IsGuideCamera => _fakeDevice.DeviceUri.AbsolutePath.Contains("GuideCam", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Guide-scope cone error, RA component in arcminutes (guide camera only). The guide scope
    /// never points exactly where the mount believes the main OTA points; the catalog star field
    /// is projected at the mount pointing offset by this. Default models a typical mini guide
    /// scope in adjustable rings (~10' total misalignment).
    /// </summary>
    internal double GuideConeErrorRaArcmin { get; set; } = 9.0;

    /// <summary>Guide-scope cone error, Dec component in arcminutes (guide camera only).</summary>
    internal double GuideConeErrorDecArcmin { get; set; } = -6.0;

    /// <summary>
    /// Guide camera roll angle in degrees (guide camera only). Real guide cams are never
    /// north-up; the guider's calibration sweep measures this angle empirically, so a non-zero
    /// default exercises that path.
    /// </summary>
    internal double GuideRotationDeg { get; set; } = 15.0;

    /// <summary>
    /// Pointing jump (arcseconds) between consecutive guide exposures beyond which the coupled
    /// mount is considered to have slewed (GOTO) rather than drifted: misalignment drift is
    /// arcseconds-per-minute, so 10 arcminutes between frames can only be a slew. On detection the
    /// zero-drift reference re-baselines to the new pointing and the in-camera star-position
    /// integrator resets — the guide cam sees a fresh field at the new target instead of the old
    /// field offset by the (sensor-dwarfing) slew distance.
    /// </summary>
    private const double SlewDetectionThresholdArcsec = 600.0;

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
    /// PE displacement amplitude in pixels (half the peak-to-peak swing).
    /// </summary>
    private double PeAmplitudePixels
    {
        get
        {
            var pixelScaleArcsec = FocalLength > 0 ? Astrometry.CoordinateUtils.PixelScaleArcsec(PixelSizeX, FocalLength) : 3.8; // ~130mm + 2.4µm default
            return PePeakTopeakArcsec / 2.0 / pixelScaleArcsec;
        }
    }

    /// <summary>
    /// PE drift rate amplitude in pixels/second.
    /// Derived from peak-to-peak arcsec → amplitude pixels → rate via ω.
    /// </summary>
    private double PeRateAmplitude => 2.0 * Math.PI / PePeriodSeconds * PeAmplitudePixels;

    /// <summary>
    /// Updates the PE component of the star position. Called each frame before rendering.
    /// STANDALONE ONLY (no coupled mount): the original wall-clock sine integration, kept for the
    /// self-contained unit-test camera. When a mount IS coupled, periodic error is the mount's
    /// responsibility — it rides on the mount's TRUE pointing (FakeSkywatcher's positional
    /// <see cref="Disturbance.Terms.PeriodicErrorTerm"/>, keyed to the RA worm encoder) and reaches
    /// the sensor through the moving projection centre (guide cam: the live mount pointing snapshot;
    /// main cam: the true-minus-believed delta). Applying it here too would double-count it
    /// (<c>_starPositionX</c> + the mount-drift term both carrying the same swing).
    /// </summary>
    private void IntegratePeDrift()
    {
        if (PePeakTopeakArcsec <= 0)
        {
            return;
        }

        // Coupled: the mount owns periodic error (see remarks). Leave _starPositionX untouched —
        // when coupled, ST-4 forwards to the mount and never writes it either, so it stays 0.
        if (ResolveCoupledMount() is not null)
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
    /// <para>
    /// Normally left for the driver to resolve itself, lazily, from the DI-provided
    /// <see cref="FakeDeviceDriverBase.External"/> on the first exposure (see
    /// <see cref="StartExposureAsync"/>) -- so nothing in the session / shared layer
    /// needs to know this fake-only dependency exists. Test code may assign it
    /// directly, which short-circuits the lazy resolve.
    /// </para>
    /// </summary>
    public ICelestialObjectDB? CelestialObjectDB { get; set; }

    // One-shot gate for the lazy catalog-DB resolve in StartExposureAsync.
    // 0 = open (not yet claimed), 1 = claimed (resolve in-flight or finished).
    // Claimed atomically via Interlocked so at most one caller ever runs the
    // (idempotent) resolve even if two exposures overlap -- the resolve block
    // sits BEFORE the camera-state CAS, so it is not otherwise serialised. The
    // gate is reset to 0 only on cancellation, so a genuinely transient OCE stays
    // retryable while a real failure (DB unavailable) gives up for good (avoids
    // re-awaiting every exposure).
    private int _catalogDbResolveGate;

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

    public async ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
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

        // Fake-only: the synthetic-star renderer needs the celestial catalog to
        // project real stars at the target, so the (real) plate solver in a
        // centering / focus / guider loop can match the rendered frame. Resolve
        // it lazily from the same DI-provided IExternal the rest of the app uses
        // -- the session / shared layer stays unaware of this fake-only need.
        // GetCelestialObjectDBAsync performs the (idempotent) DB init, so by the
        // time StopExposureCore renders, the catalog is loaded. Explicit test
        // wiring of CelestialObjectDB short-circuits this; on failure the synth
        // falls back to a random star field (the prior behaviour).
        // Claim the resolve atomically: only the caller that flips the gate 0 -> 1
        // runs it. The CelestialObjectDB null-check short-circuits the common
        // steady-state path (already resolved) so we don't touch the gate once set.
        if (CelestialObjectDB is null
            && Interlocked.CompareExchange(ref _catalogDbResolveGate, 1, 0) == 0)
        {
            try
            {
                CelestialObjectDB = await External.GetCelestialObjectDBAsync(cancellationToken).ConfigureAwait(false);
                // Success: leave the gate claimed (1) -- never re-attempt.
            }
            catch (OperationCanceledException)
            {
                // The token was cancelled mid-resolve (e.g. session abort during
                // the first exposure). This is transient and tied to THIS call,
                // not a permanent property of the catalog: release the gate so a
                // later exposure on this (reused) driver retries, and let the
                // cancellation propagate instead of arming an exposure on a dead
                // token.
                Interlocked.Exchange(ref _catalogDbResolveGate, 0);
                throw;
            }
            catch (Exception ex)
            {
                // Genuine failure (e.g. catalog not installed). Leave the gate
                // claimed so we don't re-await every exposure; the synth falls
                // back to a random star field (the prior behaviour).
                Logger.LogDebug(ex, "FakeCamera: catalog DB resolve failed; synthetic frames will use a random star field");
            }
        }

        // Fake-only (guide camera): snapshot the coupled mount's current pointing so
        // the (sync) render path can offset the guide star by the mount's drift.
        // Reading RA/Dec is async (per-call SOFA recompute) and StopExposureCore is a
        // sync timer callback, so we cache here and consume it at render time. The
        // first successful snapshot also fixes the zero-drift reference, so the star
        // starts centred at acquisition and drifts thereafter -- exactly the residual
        // a real polar-misaligned mount leaves for the guider to chase. Main imaging
        // cameras (IsGuideCamera == false) never take this path. OperationCanceledException
        // propagates (the exposure must not arm on a cancelled token); any other fault
        // falls back to the self-contained PE+ST-4 drift for this frame.
        if (IsGuideCamera && FocalLength > 0 && ResolveCoupledMount() is { } coupledMount)
        {
            try
            {
                // The catalog projection centre and the drift reference are J2000 quantities;
                // read the pointing via the frame-converting helper — a raw native (JNOW) read
                // here would shift the rendered sky by ~22' of precession at epoch 2026, which
                // is exactly the offset that wedged plate-solve centering. The transform is
                // built once and its clock refreshed per exposure (updateTime: true).
                // The sensor sees the TRUE sky: prefer the fake mount's hidden-error seam
                // (polar misalignment, post-sync drift) over the public believed read - a
                // real mount only exposes the believed pointing, where the two coincide.
                _coupledMountTransform ??= await coupledMount.TryGetTransformAsync(cancellationToken).ConfigureAwait(false);
                if (_coupledMountTransform is not { } mountTransform
                    || await ReadMountPointingJ2000Async(coupledMount, mountTransform, cancellationToken).ConfigureAwait(false) is not { } pointing)
                {
                    throw new InvalidOperationException("mount pointing unavailable in J2000 (transform unavailable)");
                }
                var (mountRa, mountDec) = pointing;

                // Worm-gear periodic error rides on the mount's TRUE pointing now (FakeSkywatcher's
                // positional PeriodicErrorTerm, keyed to its own RA worm encoder), so the swing is
                // already in (mountRa, mountDec) above and reaches the sensor through the live
                // projection centre — no encoder snapshot needed here, and IntegratePeDrift no longer
                // applies camera-side PE when a mount is coupled (it would double-count this term).
                var slewDetected = false;
                lock (_lock)
                {
                    if (_mountPointingValid && IsSlewSizedJump(_mountCachedRa, _mountCachedDec, mountRa, mountDec))
                    {
                        // GOTO between exposures: re-baseline the zero-drift reference and reset
                        // the in-camera star integrator (ST-4 corrections + PE accumulated for the
                        // OLD field are meaningless at the new pointing). Without this the random
                        // star field rendered "drift" equal to the whole slew — tens of thousands
                        // of pixels — leaving every post-slew guide frame starless.
                        _starPositionX = 0;
                        _starPositionY = 0;
                        slewDetected = true;
                    }

                    _mountCachedRa = mountRa;
                    _mountCachedDec = mountDec;
                    _mountPointingValid = true;
                    if (!_mountRefCaptured || slewDetected)
                    {
                        _mountRefRa = mountRa;
                        _mountRefDec = mountDec;
                        _mountRefCaptured = true;
                    }
                }
                if (slewDetected)
                {
                    Logger.LogInformation(
                        "FakeCamera: GOTO detected (pointing jumped to RA={Ra:F4}h Dec={Dec:F4}°); re-baselined zero-drift reference and reset star integrator.",
                        mountRa, mountDec);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "FakeCamera: mount pointing read failed; guide star falls back to self-contained drift this frame");
            }
        }

        // Fake-only (main imaging camera): snapshot the (true - believed) pointing delta
        // so the (sync) render path can shift the stamped-Target projection centre to the
        // TRUE sky the sensor would see (see _trueMinusBelievedRa). Without this, a main
        // frame would render exactly at the believed pointing and plate solving could
        // never reveal the mount's hidden cone/polar error. OperationCanceledException
        // propagates; any other fault leaves the previous delta in place for this frame.
        if (!IsGuideCamera && FocalLength > 0 && Target is not null
            && ResolveCoupledMount() is IFakeTruePointingSource trueSource and IMountDriver trueMount)
        {
            try
            {
                _coupledMountTransform ??= await trueMount.TryGetTransformAsync(cancellationToken).ConfigureAwait(false);
                if (_coupledMountTransform is { } mountTransform
                    && await trueMount.GetRaDecJ2000Async(mountTransform, updateTime: true, cancellationToken).ConfigureAwait(false) is { } believed
                    // Time already refreshed by the believed read; both legs must share the
                    // same transform epoch or the delta picks up a spurious offset.
                    && await trueSource.GetTruePointingJ2000Async(mountTransform, updateTime: false, cancellationToken).ConfigureAwait(false) is { } truePointing)
                {
                    // Wrap the RA delta into (-12, 12]h so a believed/true pair straddling
                    // the 0/24h seam never produces a spurious ~24h jump.
                    var dRaHours = truePointing.RaJ2000 - believed.RaJ2000;
                    if (dRaHours > 12.0) dRaHours -= 24.0;
                    else if (dRaHours < -12.0) dRaHours += 24.0;
                    var dDecDeg = truePointing.DecJ2000 - believed.DecJ2000;
                    lock (_lock)
                    {
                        _trueMinusBelievedRa = dRaHours;
                        _trueMinusBelievedDec = dDecDeg;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug(ex, "FakeCamera: true-pointing delta read failed; main frame renders at the stamped Target this frame");
            }
        }

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

            return startTime;
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
                    var exposureSec = current.IntendedDuration.TotalSeconds;
                    // Defocus = |FocusPosition - TrueBestFocus|. FocusPosition <= 0
                    // means the focuser was never wired up (e.g. guide camera
                    // with no dedicated focuser); render in perfect focus.
                    var defocus = FocusPosition <= 0 ? 0 : Math.Abs(FocusPosition - TrueBestFocus);

                    // Projection centre for the catalog star field. Main imaging cameras render at
                    // their stamped Target - a believed-pointing quantity - shifted by the
                    // (true - believed) delta snapshot so the frame shows the TRUE sky including
                    // the coupled mount's hidden cone/polar error (the session's centering loop
                    // plate-solves that offset and syncs it away). The guide camera has no Target:
                    // it renders at the coupled mount's LIVE true pointing (snapshot taken in
                    // StartExposureAsync) offset by the guide scope's cone error, with the camera's
                    // roll angle applied - so the field is correct at any pointing and mount drift
                    // shows up as field motion automatically. In that branch the star offset must
                    // be the in-camera part only (PE + ST-4); the mount-drift term already lives
                    // in the moving projection centre.
                    var pointingRa = 0.0;
                    var pointingDec = 0.0;
                    var hasPointing = false;
                    var rotationDeg = 0.0;
                    if (Target is { } target)
                    {
                        double trueDeltaRa, trueDeltaDec;
                        lock (_lock)
                        {
                            trueDeltaRa = _trueMinusBelievedRa;
                            trueDeltaDec = _trueMinusBelievedDec;
                        }
                        pointingRa = (target.RA + trueDeltaRa) % 24.0;
                        if (pointingRa < 0.0) pointingRa += 24.0;
                        pointingDec = Math.Clamp(target.Dec + trueDeltaDec, -90.0, 90.0);
                        hasPointing = true;
                    }
                    else if (IsGuideCamera)
                    {
                        lock (_lock)
                        {
                            if (_mountPointingValid)
                            {
                                var cosPointingDec = Math.Max(Math.Cos(_mountCachedDec * Math.PI / 180.0), 0.01);
                                pointingRa = _mountCachedRa + GuideConeErrorRaArcmin / (60.0 * 15.0 * cosPointingDec);
                                pointingDec = Math.Clamp(_mountCachedDec + GuideConeErrorDecArcmin / 60.0, -90.0, 90.0);
                                hasPointing = true;
                            }
                        }
                        rotationDeg = GuideRotationDeg;
                    }

                    LastCatalogRenderCentre = CelestialObjectDB is not null && hasPointing && FocalLength > 0
                        ? (pointingRa, pointingDec)
                        : null;

                    if (CelestialObjectDB is { } db && hasPointing && FocalLength > 0)
                    {
                        // Synth flux scales with collecting area (aperture^2)
                        // referenced to a 50mm light bucket -- a 200mm f/3 OTA
                        // collects 16x more photons than a 50mm mini-guider at
                        // the same exposure. The magnitude cutoff is then SNR-
                        // derived: include exactly the stars whose peak per-
                        // pixel ADU clears FindStarsAsync's snrMin=5 detection
                        // floor (matched with the renderer's defaults: FWHM=2px,
                        // readNoise=5). This keeps "stars in the synth" aligned
                        // with "stars the detector can find" -- the previous
                        // photon-budget formula projected mag-15 stars at 5s/
                        // 200mm and gave the catalog plate solver 1600+
                        // candidates against ~30 detections, blowing the
                        // proximity matcher's tolerance and timing rungs out.
                        // Aperture is denormalised onto the camera in
                        // Session.Lifecycle and the polar-alignment
                        // AppSignalHandler. Without it, scale=1.0 (50mm) and
                        // existing standalone FakeCameraDriver tests keep
                        // their previous brightness budget.
                        var apertureScale = Aperture is int apertureMm and > 0
                            ? Math.Pow(apertureMm / 50.0, 2.0)
                            : 1.0;
                        var magCutoff = Math.Min(15.0,
                            SyntheticStarFieldRenderer.DetectabilityMagCutoff(
                                apertureScale, exposureSec));
                        var stars = SyntheticStarFieldRenderer.ProjectCatalogStars(
                            pointingRa, pointingDec, FocalLength, PixelSizeX, imgWidth, imgHeight, db, magCutoff,
                            rotationDeg: rotationDeg);
                        // Diagnostic: confirm aperture / scale / cutoff / cap during synth render.
                        Logger.LogDebug(
                            "FakeCamera synth: aperture={Aperture} focal={FocalLength} t={ExposureSec:F3}s defocus={Defocus} scale={Scale:F2} magCutoff={Cutoff:F2} placedStars={Stars} rot={RotationDeg:F1}",
                            Aperture, FocalLength, exposureSec, defocus, apertureScale, magCutoff, stars.Count, rotationDeg);
                        var (offsetX, offsetY) = Target is not null ? TotalStarOffset : InCameraStarOffset;
                        var cloudSeed = _frameRng.Next();
                        var starSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stars);
                        array = SensorType is Imaging.SensorType.RGGB
                            ? SyntheticStarFieldRenderer.RenderBayer(imgWidth, imgHeight, defocus, starSpan,
                                offsetX: offsetX, offsetY: offsetY,
                                exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                                apertureScaleFactor: apertureScale, dest: dest)
                            : SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocus,
                                stars: starSpan, offsetX: offsetX, offsetY: offsetY,
                                exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                                cloudCoverage: CloudCoverage, cloudSeed: cloudSeed,
                                apertureScaleFactor: apertureScale, dest: dest);
                    }
                    else
                    {
                        // No catalog binding -- random star field. Logged at Debug
                        // because legacy guide-cam / unit-test scenarios fall
                        // here intentionally; it's only suspect inside a
                        // session/polar-align flow that *should* have wired up
                        // a catalog.
                        Logger.LogDebug(
                            "FakeCamera synth: no catalog binding (catalogDb={HasDb} target={HasTarget} focalLen={FocalLength}) -- rendering 50 random stars",
                            CelestialObjectDB is not null, Target is not null, FocalLength);
                        var cloudSeed = _frameRng.Next();
                        array = SyntheticStarFieldRenderer.Render(imgWidth, imgHeight, defocusSteps: defocus,
                            offsetX: TotalStarOffset.X, offsetY: TotalStarOffset.Y,
                            exposureSeconds: exposureSec, noiseSeed: _frameRng.Next(),
                            cloudCoverage: CloudCoverage, cloudSeed: cloudSeed, dest: dest);
                    }

                    // Compute actual min/max of the rendered data. Vectorised via
                    // TensorPrimitives + a flat span -- the previous nested
                    // multidim-array loop spent ~400ms on a 61MP IMX455 frame
                    // (entirely scalar, with multi-dim bounds checks per index)
                    // and was the dominant cost on the StopExposureCore callback
                    // path, dwarfing the renderer itself. Reinterpret the
                    // float[,] as a flat ref float to side-step the missing
                    // generic GetArrayDataReference overload for multi-dim.
                    ref var firstByte = ref MemoryMarshal.GetArrayDataReference(array);
                    ref var firstFloat = ref Unsafe.As<byte, float>(ref firstByte);
                    var flatSpan = MemoryMarshal.CreateReadOnlySpan(ref firstFloat, imgHeight * imgWidth);
                    var dataMin = TensorPrimitives.Min(flatSpan);
                    var dataMax = TensorPrimitives.Max(flatSpan);

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

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        // ST-4 models a guide cable wired from this camera's port to the coupled
        // mount's autoguide port: the pulse physically moves the MOUNT (encoders and
        // pointing change; the star field follows via the live projection centre /
        // MountDriftPixels), exactly like real hardware. The previous in-camera
        // shortcut (shift the star integrator, mount untouched) closed the guide loop
        // with dynamics no real rig has — instantaneous full-magnitude corrections,
        // perfectly sensor-axis-aligned, encoders frozen — which let a neural guider
        // train on a plant that doesn't exist.
        if (ResolveCoupledMount() is { CanPulseGuide: true } mount)
        {
            await mount.PulseGuideAsync(direction, duration, cancellationToken).ConfigureAwait(false);
            return;
        }

        // No mount coupled (standalone / unit-test use): legacy in-camera shift.
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
    }

    /// <summary>
    /// Current star offset: integrated PE drift + accumulated ST-4 corrections +
    /// (guide camera only) the coupled mount's misalignment drift. All operate on
    /// the same sensor position, so the guider's ST-4 corrections push back against
    /// both the PE wobble and the mount drift.
    /// </summary>
    private (double X, double Y) TotalStarOffset
    {
        get
        {
            IntegratePeDrift();
            var (mountX, mountY) = MountDriftPixels();
            return (_starPositionX + mountX, _starPositionY + mountY);
        }
    }

    /// <summary>
    /// In-camera star offset only: integrated PE wobble + accumulated ST-4 corrections, WITHOUT
    /// the mount-drift term. Used by the guide-camera catalog render path, where the mount's
    /// pointing change (drift and slews alike) is already expressed through the live projection
    /// centre — adding <see cref="MountDriftPixels"/> on top would double-count it.
    /// </summary>
    private (double X, double Y) InCameraStarOffset
    {
        get
        {
            IntegratePeDrift();
            return (_starPositionX, _starPositionY);
        }
    }

    /// <summary>
    /// True when the pointing change between two consecutive guide exposures exceeds
    /// <see cref="SlewDetectionThresholdArcsec"/> on either axis — i.e. a GOTO, not drift.
    /// Caller must hold <c>_lock</c>.
    /// </summary>
    private static bool IsSlewSizedJump(double prevRaHours, double prevDecDeg, double raHours, double decDeg)
    {
        // Wrap the RA delta into (-12, 12]h so a jump across the 0/24h seam is not misread.
        var dRaHours = raHours - prevRaHours;
        if (dRaHours > 12.0) dRaHours -= 24.0;
        else if (dRaHours < -12.0) dRaHours += 24.0;

        var cosDec = Math.Cos(prevDecDeg * Math.PI / 180.0);
        var raArcsec = Math.Abs(dRaHours * 15.0 * 3600.0 * cosDec);
        var decArcsec = Math.Abs((decDeg - prevDecDeg) * 3600.0);
        return raArcsec > SlewDetectionThresholdArcsec || decArcsec > SlewDetectionThresholdArcsec;
    }

    /// <summary>
    /// Lazily finds the connected mount in the device hub (single-mount invariant),
    /// caching it once found. Returns <c>null</c> when no hub is registered (unit
    /// tests) or no mount is connected yet -- the guide star then uses only the
    /// self-contained PE+ST-4 drift. Resolved from the retained
    /// <see cref="FakeDeviceDriverBase.ServiceProvider"/> so nothing in the session /
    /// shared layer needs to know this fake-only coupling exists.
    /// </summary>
    /// <summary>
    /// The mount pointing the SENSOR sees, in J2000: the fake true-pointing seam
    /// (hidden polar misalignment / post-sync drift) when the coupled mount models
    /// one, else the public believed read - on real mounts the two coincide by
    /// definition (the believed read is all that exists).
    /// </summary>
    private static ValueTask<(double RaJ2000, double DecJ2000)?> ReadMountPointingJ2000Async(
        IMountDriver mount, Transform transform, CancellationToken cancellationToken)
        => mount is IFakeTruePointingSource trueSource
            ? trueSource.GetTruePointingJ2000Async(transform, updateTime: true, cancellationToken)
            : mount.GetRaDecJ2000Async(transform, updateTime: true, cancellationToken);

    private IMountDriver? ResolveCoupledMount()
    {
        if (_coupledMount is { Connected: true })
        {
            return _coupledMount;
        }

        var hub = ServiceProvider.GetService<IDeviceHub>();
        if (hub is null)
        {
            return null;
        }

        foreach (var (_, driver) in hub.ConnectedDevices)
        {
            if (driver is IMountDriver mount && mount.Connected)
            {
                _coupledMount = mount;
                return mount;
            }
        }

        return null;
    }

    /// <summary>
    /// Guide-camera-only: the star offset (px) induced by the coupled mount's
    /// misalignment drift since the zero-drift reference was captured, computed from
    /// the pointing snapshot taken in <see cref="StartExposureAsync"/> (the render path
    /// is sync and cannot await). Converts the believed-vs-current pointing delta to
    /// pixels via the guide pixel scale. Returns <c>(0, 0)</c> for main imaging cameras
    /// (no snapshot ever taken) or before a reference exists. The Dec delta maps to Y
    /// and the RA delta (scaled by cos Dec) to X, matching the ST-4 accumulator axes
    /// (N/S -> Y, E/W -> X); the sign is otherwise arbitrary because the guider learns
    /// the calibration mapping empirically, then nulls whatever it sees.
    /// </summary>
    private (double X, double Y) MountDriftPixels()
    {
        double ra, dec, refRa, refDec;
        lock (_lock)
        {
            if (!_mountPointingValid || !_mountRefCaptured)
            {
                return (0.0, 0.0);
            }
            ra = _mountCachedRa;
            dec = _mountCachedDec;
            refRa = _mountRefRa;
            refDec = _mountRefDec;
        }

        var pixelScaleArcsec = Astrometry.CoordinateUtils.PixelScaleArcsec(PixelSizeX, FocalLength);
        if (pixelScaleArcsec <= 0)
        {
            return (0.0, 0.0);
        }

        // Wrap the RA delta into (-12, 12]h so a reference near the 0/24h seam never
        // produces a spurious ~24h jump (the real drift is sub-arcminute).
        var dRaHours = ra - refRa;
        if (dRaHours > 12.0) dRaHours -= 24.0;
        else if (dRaHours < -12.0) dRaHours += 24.0;

        var cosDec = Math.Cos(refDec * Math.PI / 180.0);
        var raArcsec = dRaHours * 15.0 * 3600.0 * cosDec;
        var decArcsec = (dec - refDec) * 3600.0;
        return (raArcsec / pixelScaleArcsec, decArcsec / pixelScaleArcsec);
    }

    /// <summary>
    /// Test seam: the guide-star pixel offset currently induced by the coupled
    /// mount's misalignment drift (the value folded into <see cref="TotalStarOffset"/>
    /// for rendering). <c>(0, 0)</c> when this is not a guide camera, no mount is
    /// coupled, or no zero-drift reference has been captured yet. Reflects the mount
    /// pointing snapshot taken by the most recent <see cref="StartExposureAsync"/>.
    /// </summary>
    internal (double X, double Y) CurrentMountDriftPixels => MountDriftPixels();

    /// <summary>
    /// Test seam: J2000 projection centre (RA hours, Dec degrees) of the most recent
    /// catalog-star render — i.e. the stamped <see cref="Target"/> for main cameras, or the
    /// coupled mount's frame-converted pointing plus cone error for the guide camera. Null when
    /// the last render fell back to the random star field.
    /// </summary>
    internal (double RaJ2000, double DecJ2000)? LastCatalogRenderCentre { get; private set; }

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
