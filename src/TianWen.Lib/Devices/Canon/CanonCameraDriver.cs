using FC.SDK;
using FC.SDK.Canon;
using FC.SDK.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using TianWen.DAL;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Canon DSLR camera driver via FC.SDK (PTP over USB or WiFi).
/// Uses <see cref="CanonCamera.TakePictureAsync"/> for exposures ≤30s (Tv mode)
/// and <see cref="CanonCamera.BulbStartAsync"/>/<see cref="CanonCamera.BulbEndAsync"/> for longer exposures.
/// Images are downloaded as CR2 and decoded via the SharpAstro codecs facade (FC.SDK.Raw).
/// </summary>
internal sealed class CanonCameraDriver : ICameraDriver, IVideoCameraDriver
{
    /// <summary>Canon Tv codes for standard shutter speeds up to 30s.</summary>
    private static readonly (uint Code, TimeSpan Duration)[] TvTable =
    [
        (0x10, TimeSpan.FromSeconds(30)),
        (0x13, TimeSpan.FromSeconds(25)),
        (0x14, TimeSpan.FromSeconds(20)),
        (0x18, TimeSpan.FromSeconds(15)),
        (0x1B, TimeSpan.FromSeconds(13)),
        (0x1C, TimeSpan.FromSeconds(10)),
        (0x1D, TimeSpan.FromSeconds(10)), // some models
        (0x20, TimeSpan.FromSeconds(8)),
        (0x23, TimeSpan.FromSeconds(6)),
        (0x24, TimeSpan.FromSeconds(5)),
        (0x25, TimeSpan.FromSeconds(5)),
        (0x28, TimeSpan.FromSeconds(4)),
        (0x2B, TimeSpan.FromSeconds(3.2)),
        (0x2C, TimeSpan.FromSeconds(2.5)),
        (0x2D, TimeSpan.FromSeconds(2.5)),
        (0x30, TimeSpan.FromSeconds(2)),
        (0x33, TimeSpan.FromSeconds(1.6)),
        (0x34, TimeSpan.FromSeconds(1.3)),
        (0x35, TimeSpan.FromSeconds(1.3)),
        (0x38, TimeSpan.FromSeconds(1)),
        (0x3B, TimeSpan.FromSeconds(0.8)),
        (0x3C, TimeSpan.FromSeconds(0.6)),
        (0x3D, TimeSpan.FromSeconds(0.6)),
        (0x40, TimeSpan.FromSeconds(0.5)),
        (0x43, TimeSpan.FromSeconds(0.4)),
        (0x44, TimeSpan.FromSeconds(0.3)),
        (0x45, TimeSpan.FromSeconds(0.3)),
        (0x48, TimeSpan.FromSeconds(1.0 / 4)),
        (0x4B, TimeSpan.FromSeconds(1.0 / 5)),
        (0x4C, TimeSpan.FromSeconds(1.0 / 6)),
        (0x4D, TimeSpan.FromSeconds(1.0 / 6)),
        (0x50, TimeSpan.FromSeconds(1.0 / 8)),
        (0x53, TimeSpan.FromSeconds(1.0 / 10)),
        (0x54, TimeSpan.FromSeconds(1.0 / 10)),
        (0x55, TimeSpan.FromSeconds(1.0 / 13)),
        (0x58, TimeSpan.FromSeconds(1.0 / 15)),
        (0x5B, TimeSpan.FromSeconds(1.0 / 20)),
        (0x5C, TimeSpan.FromSeconds(1.0 / 20)),
        (0x5D, TimeSpan.FromSeconds(1.0 / 25)),
        (0x60, TimeSpan.FromSeconds(1.0 / 30)),
        (0x63, TimeSpan.FromSeconds(1.0 / 40)),
        (0x64, TimeSpan.FromSeconds(1.0 / 45)),
        (0x65, TimeSpan.FromSeconds(1.0 / 50)),
        (0x68, TimeSpan.FromSeconds(1.0 / 60)),
        (0x6B, TimeSpan.FromSeconds(1.0 / 80)),
        (0x6C, TimeSpan.FromSeconds(1.0 / 90)),
        (0x6D, TimeSpan.FromSeconds(1.0 / 100)),
        (0x70, TimeSpan.FromSeconds(1.0 / 125)),
        (0x73, TimeSpan.FromSeconds(1.0 / 160)),
        (0x74, TimeSpan.FromSeconds(1.0 / 180)),
        (0x75, TimeSpan.FromSeconds(1.0 / 200)),
        (0x78, TimeSpan.FromSeconds(1.0 / 250)),
        (0x7B, TimeSpan.FromSeconds(1.0 / 320)),
        (0x7C, TimeSpan.FromSeconds(1.0 / 350)),
        (0x7D, TimeSpan.FromSeconds(1.0 / 400)),
        (0x80, TimeSpan.FromSeconds(1.0 / 500)),
        (0x83, TimeSpan.FromSeconds(1.0 / 640)),
        (0x84, TimeSpan.FromSeconds(1.0 / 750)),
        (0x85, TimeSpan.FromSeconds(1.0 / 800)),
        (0x88, TimeSpan.FromSeconds(1.0 / 1000)),
        (0x8B, TimeSpan.FromSeconds(1.0 / 1250)),
        (0x8C, TimeSpan.FromSeconds(1.0 / 1500)),
        (0x8D, TimeSpan.FromSeconds(1.0 / 1600)),
        (0x90, TimeSpan.FromSeconds(1.0 / 2000)),
        (0x93, TimeSpan.FromSeconds(1.0 / 2500)),
        (0x94, TimeSpan.FromSeconds(1.0 / 3000)),
        (0x95, TimeSpan.FromSeconds(1.0 / 3200)),
        (0x98, TimeSpan.FromSeconds(1.0 / 4000)),
    ];

    /// <summary>Canon ISO codes.</summary>
    private static readonly (uint Code, string Label)[] IsoTable =
    [
        (0x00000048, "ISO 100"),
        (0x0000004B, "ISO 125"),
        (0x0000004D, "ISO 160"),
        (0x00000050, "ISO 200"),
        (0x00000053, "ISO 250"),
        (0x00000055, "ISO 320"),
        (0x00000058, "ISO 400"),
        (0x0000005B, "ISO 500"),
        (0x0000005D, "ISO 640"),
        (0x00000060, "ISO 800"),
        (0x00000063, "ISO 1000"),
        (0x00000065, "ISO 1250"),
        (0x00000068, "ISO 1600"),
        (0x0000006B, "ISO 2000"),
        (0x0000006D, "ISO 2500"),
        (0x00000070, "ISO 3200"),
        (0x00000073, "ISO 4000"),
        (0x00000075, "ISO 5000"),
        (0x00000078, "ISO 6400"),
        (0x0000007B, "ISO 8000"),
        (0x0000007D, "ISO 10000"),
        (0x00000080, "ISO 12800"),
        (0x00000083, "ISO 16000"),
        (0x00000085, "ISO 20000"),
        (0x00000088, "ISO 25600"),
    ];

    // Known Canon sensor pixel sizes (µm) keyed by model substring
    private static readonly (string Model, double PixelSize, int Width, int Height)[] SensorTable =
    [
        ("6D",    6.55, 5472, 3648),
        ("5D Mark IV", 5.36, 6720, 4480),
        ("5D Mark III", 6.25, 5760, 3840),
        ("5D Mark II", 6.41, 5616, 3744),
        ("80D",   3.7,  6000, 4000),
        ("77D",   3.7,  6000, 4000),
        ("7D Mark II", 4.1, 5472, 3648),
        ("70D",   4.1,  5472, 3648),
        ("60D",   4.3,  5184, 3456),
        ("2000D", 4.3,  6000, 4000),
        ("1300D", 4.3,  5184, 3456),
        ("R5",    4.39, 8192, 5464),
        ("R6",    6.23, 5472, 3648),
        ("Ra",    6.55, 5472, 3648), // astro-modified EOS Ra
    ];

    private readonly CanonDevice _device;
    private readonly IExternal _external;
    private readonly CanonCameraFactory _cameraFactory;
    private CanonCamera? _camera;
    private bool _connected;
    private bool _bulbActive;
    private TaskCompletionSource<uint>? _objectAddedTcs;
    private Task? _downloadTask;

    // Live View (IVideoCameraDriver) single-stream gate: 0/1. Streaming and single-shot StartExposureAsync
    // are mutually exclusive (the camera is in one mode), mirroring FakeCameraDriver's "stream OR expose" rule.
    private int _videoActive;

    // Image state
    private Channel? _lastImageData;
    private DateTimeOffset? _lastExposureStartTime;
    private TimeSpan? _lastExposureDuration;
    private FrameType _lastExposureFrameType;
    private int _cameraState = (int)CameraState.Idle;

    // ISO state
    private short _currentIsoIndex;
    private readonly IReadOnlyList<string> _gains = IsoTable.Select(i => i.Label).ToArray();

    // Sensor info (populated from SensorTable or first image decode)
    private string? _sensorModel;
    private double _pixelSizeX = 6.55; // default: 6D
    private double _pixelSizeY = 6.55;
    private int _cameraXSize = 5472;
    private int _cameraYSize = 3648;

    public CanonCameraDriver(CanonDevice device, IServiceProvider serviceProvider, CanonCameraFactory cameraFactory)
    {
        _device = device;
        _external = serviceProvider.GetRequiredService<IExternal>();
        _cameraFactory = cameraFactory;
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(CanonCameraDriver));
        TimeProvider = serviceProvider.GetRequiredService<ITimeProvider>();
    }

    // --- IDeviceDriver ---
    public string Name => _device.DisplayName;
    public string? Description => _device.IsWifi ? "Canon DSLR (WiFi/PTP-IP)" : "Canon DSLR (USB)";
    public string? DriverInfo => Description;
    public string? DriverVersion => "1.0";
    public DeviceType DriverType => DeviceType.Camera;
    public IExternal External => _external;
    public ILogger Logger { get; }
    public ITimeProvider TimeProvider { get; }
    public bool Connected => Volatile.Read(ref _connected);
    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Connected)
        {
            return;
        }

        // Connect based on transport type
        if (_device.IsWpd && OperatingSystem.IsWindows())
        {
            _camera = _cameraFactory.ConnectWpd(_device.DeviceId);
        }
        else if (_device.IsWifi)
        {
            var host = _device.WifiHost
                ?? throw new InvalidOperationException("WiFi host not configured. Set the IP address in Equipment settings.");
            _camera = _cameraFactory.ConnectWifi(host, "TianWen");
        }
        else
        {
            // Find matching USB camera by device ID
            var deviceId = _device.DeviceId;
            UsbDeviceInfo? match = null;
            foreach (var usb in CanonCamera.EnumerateUsbCameras())
            {
                var id = !string.IsNullOrEmpty(usb.SerialNumber) ? usb.SerialNumber
                    : !string.IsNullOrEmpty(usb.DevicePath) ? usb.DevicePath
                    : $"{usb.VendorId:X4}:{usb.ProductId:X4}";
                if (id == deviceId)
                {
                    match = usb;
                    break;
                }
            }

            _camera = match is { } m
                ? _cameraFactory.ConnectUsb(m)
                : throw new InvalidOperationException($"Canon camera with ID '{deviceId}' not found on USB.");
        }

        var result = await _camera.OpenSessionAsync(cancellationToken);
        if (result is not EdsError.OK)
        {
            throw new CanonDriverException(result, "Failed to open PTP session");
        }

        _camera.StartEventPolling();
        _camera.ObjectAdded += OnObjectAdded;

        // Populate sensor info from model name
        var modelName = _device.DisplayName;
        foreach (var (model, pixelSize, width, height) in SensorTable)
        {
            if (modelName.Contains(model, StringComparison.OrdinalIgnoreCase))
            {
                _sensorModel = $"Canon_{model.Replace(" ", "")}";
                _pixelSizeX = pixelSize;
                _pixelSizeY = pixelSize;
                _cameraXSize = width;
                _cameraYSize = height;
                break;
            }
        }

        // Read current ISO to set initial index
        try
        {
            var (err, isoValue) = await _camera.GetPropertyAsync(EdsPropertyId.ISOSpeed, cancellationToken);
            if (err is EdsError.OK)
            {
                for (short i = 0; i < IsoTable.Length; i++)
                {
                    if (IsoTable[i].Code == isoValue)
                    {
                        _currentIsoIndex = i;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read current ISO from Canon camera");
        }

        // Apply astrophotography-friendly defaults. Each setter is best-effort; older
        // bodies reject some properties (returns non-OK EdsError), and a single
        // unsupported one must not fail the connect. Logged at Info on success so the
        // user can see in the log which defaults took effect.
        //
        // SaveTo=Host:        images download to host, not SD card (session-length safe)
        // AutoPowerOff=0:     disable the 30-min sleep that would kill unattended runs
        // AFMode=ManualFocus: prevent AF hunting on dark sky between exposures
        // HighIsoNR=Disable:  in-camera NR is wrong for stacking; calibrate in post
        await TrySetAsync(
            () => _camera.SetSaveToAsync(EdsSaveTo.Host, cancellationToken),
            "SaveTo=Host");
        await TrySetAsync(
            () => _camera.SetAutoPowerOffAsync(0, cancellationToken),
            "AutoPowerOff=disabled");
        await TrySetAsync(
            () => _camera.SetAFModeAsync(EdsAFMode.ManualFocus, cancellationToken),
            "AFMode=ManualFocus");
        await TrySetAsync(
            () => _camera.SetHighIsoNRAsync(EdsHighIsoNR.Disable, cancellationToken),
            "HighIsoNR=Disable");

        // Long-exposure NR lives in Custom Functions on Canon DSLRs, not as a direct
        // PTP property. Leaving it on doubles every sub (in-camera dark subtraction);
        // proper calibration frames give better results anyway.
        await DisableLongExposureNRAsync(cancellationToken);

        // Enable mirror lockup for astrophotography (reduces vibration during exposures)
        try
        {
            var (mluErr, mluSetting) = await _camera.GetMirrorUpSettingAsync(cancellationToken);
            if (mluErr is EdsError.OK && mluSetting is EdsMirrorUpSetting.Off)
            {
                var enableResult = await _camera.EnableMirrorLockupAsync(cancellationToken);
                if (enableResult is EdsError.OK)
                {
                    Logger.LogInformation("Mirror lockup enabled automatically for astrophotography");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not configure mirror lockup on Canon camera");
        }

        Volatile.Write(ref _connected, true);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        Logger.LogInformation("Canon camera connected: {Name} ({Transport})", Name, _device.IsWifi ? "WiFi" : "USB");
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_camera is { } camera)
        {
            camera.ObjectAdded -= OnObjectAdded;
            await camera.StopEventPollingAsync();
            await camera.CloseSessionAsync(cancellationToken);
            await camera.DisposeAsync();
            _camera = null;
        }

        Volatile.Write(ref _connected, false);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
    }

    // --- ICameraDriver capability flags ---
    public bool CanGetCoolerPower => false;
    public bool CanGetCoolerOn => false;
    public bool CanSetCoolerOn => false;
    public bool CanGetCCDTemperature => false;
    public bool CanSetCCDTemperature => false;
    public bool CanGetHeatsinkTemperature => false;
    public bool CanStopExposure => false;
    public bool CanAbortExposure => true; // bulb can be aborted
    public bool CanFastReadout => false;
    public bool CanSetBitDepth => false;
    public bool CanPulseGuide => false;
    public bool CanMirrorLockup => true;
    public bool UsesGainValue => false;
    public bool UsesGainMode => true; // ISO via mode list
    public bool UsesOffsetValue => false;
    public bool UsesOffsetMode => false;

    // --- Sensor geometry ---
    public double PixelSizeX => _pixelSizeX;
    public double PixelSizeY => _pixelSizeY;
    public short MaxBinX => 1; // DSLRs don't support binning
    public short MaxBinY => 1;
    public int BinX { get; set; } = 1;
    public int BinY { get; set; } = 1;
    public int StartX { get; set; }
    public int StartY { get; set; }
    public int NumX { get; set; }
    public int NumY { get; set; }
    public int CameraXSize => _cameraXSize;
    public int CameraYSize => _cameraYSize;
    public int MaxADU => 16383; // 14-bit Canon sensor
    public double FullWellCapacity => 70000; // typical Canon full-frame
    public double ElectronsPerADU => 4.3; // typical Canon 6D
    public double ExposureResolution => 0.001; // 1ms

    // --- Sensor type ---
    public string? SensorModelName => _sensorModel;
    public SensorType SensorType => SensorType.RGGB;
    public int BayerOffsetX => 0;
    public int BayerOffsetY => 0;

    // --- Gain (ISO) ---
    public IReadOnlyList<string> Gains => _gains;
    public short GainMin => 0;
    public short GainMax => (short)(_gains.Count - 1);

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_currentIsoIndex);

    public async ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
    {
        if (value < 0 || value >= IsoTable.Length || _camera is null)
        {
            return;
        }

        var result = await _camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, IsoTable[value].Code, cancellationToken);
        if (result is EdsError.OK)
        {
            _currentIsoIndex = value;
        }
    }

    // --- Offset (not supported) ---
    public IReadOnlyList<string> Offsets => [];
    public int OffsetMin => 0;
    public int OffsetMax => 0;
    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    public ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // --- Readout / bit depth ---
    public ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    public ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<BitDepth?>(BitDepth.Int16);
    public ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // --- Thermal (not supported) ---
    public ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(double.NaN);
    public ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(double.NaN);
    public ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(double.NaN);
    public ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(double.NaN);
    public ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // --- Mirror lockup ---
    public async ValueTask<bool> GetMirrorLockupAsync(CancellationToken cancellationToken = default)
    {
        if (_camera is null)
        {
            return false;
        }

        var (err, setting) = await _camera.GetMirrorUpSettingAsync(cancellationToken);
        return err is EdsError.OK && setting is EdsMirrorUpSetting.On;
    }

    public async ValueTask SetMirrorLockupAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (_camera is null)
        {
            return;
        }

        var result = value
            ? await _camera.EnableMirrorLockupAsync(cancellationToken)
            : await _camera.DisableMirrorLockupAsync(cancellationToken);

        if (result is EdsError.OK)
        {
            Logger.LogInformation("Canon mirror lockup {State}", value ? "enabled" : "disabled");
        }
        else
        {
            Logger.LogWarning("Failed to {Action} Canon mirror lockup: {Error}", value ? "enable" : "disable", result);
        }
    }

    // --- Pulse guiding (not supported) ---
    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    // --- Exposure state ---
    public DateTimeOffset? LastExposureStartTime => _lastExposureStartTime;
    public TimeSpan? LastExposureDuration => _lastExposureDuration;
    public FrameType LastExposureFrameType => _lastExposureFrameType;
    public Channel? ImageData => _lastImageData;

    public void ReleaseImageData()
    {
        _lastImageData = null;
    }

    public ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult((CameraState)Volatile.Read(ref _cameraState) == CameraState.Idle && _lastImageData is not null);

    public ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult((CameraState)Volatile.Read(ref _cameraState));

    // --- Image metadata (set by session controller) ---
    public string? Telescope { get; set; }
    public int FocalLength { get; set; }
    public int? Aperture { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public double? SiteElevation { get; set; }
    public Filter Filter { get; set; }
    public int FocusPosition { get; set; }
    public Target? Target { get; set; }

    /// <inheritdoc />
    public Imaging.GuidingStats? GuideStats { get; set; }

    // --- Exposure ---
    public async ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        if (_camera is null)
        {
            throw new InvalidOperationException("Camera not connected");
        }

        if (Volatile.Read(ref _videoActive) == 1)
        {
            throw new InvalidOperationException(
                "Cannot start a single-shot exposure while a Canon Live View video stream is running.");
        }

        var startTime = TimeProvider.GetUtcNow();
        _lastExposureStartTime = startTime;
        _lastExposureDuration = duration;
        _lastExposureFrameType = frameType;
        _lastImageData = null;

        // Prepare to receive ObjectAdded event
        _objectAddedTcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _cameraState, (int)CameraState.Exposing);

        if (duration <= TimeSpan.FromSeconds(30))
        {
            // Tv mode: set shutter speed then take picture
            var tvCode = FindClosestTv(duration);
            await _camera.SetPropertyAsync(EdsPropertyId.Tv, tvCode, cancellationToken);
            await _camera.TakePictureAsync(cancellationToken);
        }
        else
        {
            // Bulb mode
            _bulbActive = true;
            await _camera.BulbStartAsync(cancellationToken);
            await TimeProvider.SleepAsync(duration, cancellationToken);
            await _camera.BulbEndAsync(cancellationToken);
            _bulbActive = false;
        }

        // Start background download once ObjectAdded fires
        _downloadTask = Task.Run(() => WaitAndDownloadAsync(cancellationToken), cancellationToken);

        return startTime;
    }

    public ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask; // not supported

    public async ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        if (_bulbActive && _camera is not null)
        {
            await _camera.BulbEndAsync(cancellationToken);
            _bulbActive = false;
        }
        _objectAddedTcs?.TrySetCanceled(cancellationToken);
        Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
    }

    private async Task WaitAndDownloadAsync(CancellationToken ct)
    {
        if (_objectAddedTcs is null || _camera is null)
        {
            return;
        }

        uint handle;
        try
        {
            handle = await _objectAddedTcs.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
            return;
        }

        Interlocked.Exchange(ref _cameraState, (int)CameraState.Download);

        var tmpPath = Path.Combine(Path.GetTempPath(), $"tianwen_canon_{Guid.NewGuid():N}.cr2");
        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await _camera.DownloadAsync(handle, fs, ct);
            }
            await _camera.TransferCompleteAsync(handle, ct);

            if (Image.TryReadImageFile(tmpPath, out var image))
            {
                _lastImageData = new Channel(image.GetChannelArray(0), Filter.None, image.MinValue, image.MaxValue, 0);

                // Update sensor dimensions from actual image if not set from model table
                if (_cameraXSize <= 0)
                {
                    _cameraXSize = image.Width;
                    _cameraYSize = image.Height;
                }

                Logger.LogDebug("Canon image downloaded: {W}x{H}", image.Width, image.Height);
            }
            else
            {
                Logger.LogError("Failed to decode CR2 from Canon camera");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Canon image download failed");
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
        }
    }

    private void OnObjectAdded(object? sender, CanonObjectAddedEventArgs e)
    {
        _objectAddedTcs?.TrySetResult(e.ObjectHandle);
    }

    /// <summary>Applies a Canon setter, logging Info on OK and Debug on reject.</summary>
    private async ValueTask TrySetAsync(Func<Task<EdsError>> setter, string name)
    {
        try
        {
            var result = await setter();
            if (result is EdsError.OK)
            {
                Logger.LogInformation("Canon {Setting} applied", name);
            }
            else
            {
                Logger.LogDebug("Canon {Setting} rejected: {Error}", name, result);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Canon {Setting} failed", name);
        }
    }

    /// <summary>
    /// Reads the C.Fn block, flips Long Exposure NR to Off, and writes it back. Silent
    /// no-op when this body keeps the setting somewhere other than a C.Fn, when its C.Fn
    /// ID has not been verified against real hardware, or when it exposes no C.Fn block.
    /// </summary>
    /// <remarks>
    /// The ID comes from <see cref="CanonCustomFunctionId.LongExposureNrIdFor"/>, which owns the
    /// per-model table. This used to try two IDs of its own, a "6D" one and a "Rebel" one, and both
    /// were guesses: on the 6D long-exposure NR is a plain shooting-menu property with no C.Fn ID at
    /// all. So the call could only ever no-op, or write into whatever function a body happened to
    /// keep at that address. FC.SDK deleted both constants and grew this resolver instead, which is
    /// the right home for it: a C.Fn ID is camera-specific, so guessing one is never safe.
    /// </remarks>
    private async ValueTask DisableLongExposureNRAsync(CancellationToken ct)
    {
        if (_camera is null)
        {
            return;
        }

        try
        {
            if (CanonCustomFunctionId.LongExposureNrIdFor(_camera.Model) is not { } cfnId)
            {
                Logger.LogDebug("Canon LongExposureNR: no verified C.Fn ID for {Model}", _camera.Model);
                return;
            }

            var (err, block) = await _camera.GetCustomFunctionBlockAsync(ct);
            if (err is not EdsError.OK || block is null)
            {
                Logger.LogDebug("Canon LongExposureNR: C.Fn block read failed ({Error})", err);
                return;
            }

            if (!block.SetValue(cfnId, (uint)EdsLongExposureNR.Off))
            {
                Logger.LogDebug("Canon LongExposureNR: C.Fn ID not present on this body");
                return;
            }

            var writeErr = await _camera.SetCustomFunctionBlockAsync(block, ct);
            if (writeErr is EdsError.OK)
            {
                Logger.LogInformation("Canon LongExposureNR=Off applied");
            }
            else
            {
                Logger.LogDebug("Canon LongExposureNR write rejected: {Error}", writeErr);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Canon LongExposureNR disable failed");
        }
    }

    private static uint FindClosestTv(TimeSpan duration)
    {
        var seconds = duration.TotalSeconds;
        uint bestCode = TvTable[0].Code;
        var bestDiff = double.MaxValue;

        foreach (var (code, tvDuration) in TvTable)
        {
            var diff = Math.Abs(tvDuration.TotalSeconds - seconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestCode = code;
            }
        }

        return bestCode;
    }

    // ── Live View video (IVideoCameraDriver) ─────────────────────────────────────
    // Canon EOS bodies stream a host feed only as Live View (EVF) JPEG: a camera-processed
    // (demosaiced + white-balanced + tone-mapped) RGB frame, ~1024x680, at the EVF's own ~15-30 fps.
    // We decode each frame straight from the SDK byte[] into a 3-channel [0,1] Image (Image.TryDecodeRaster,
    // no temp-file round-trip) and yield it; the planetary live-stack pipeline consumes it as a colour master.
    //
    // The 5x/10x magnified feed IS the planetary regime: at 5x the body sends a near-1:1-pixel crop of a
    // small sensor region instead of a downscaled whole frame, and that crop is pannable, which makes it the
    // host-side ROI jog the COM-recenter loop wants. Both are PTP operations (0x9158 zoom / 0x9159 pan) and
    // the resulting crop arrives as a record inside the live-view frame. There is no Evf_ZoomPosition or
    // Evf_ZoomRect property to read, which is why this sat deferred behind a request for an accessor that
    // could never exist; FC.SDK 3.0 ships the operations, so it is wired here.
    //
    // Four behaviours measured on an EOS 6D that the wiring has to respect, every one of which fails
    // SILENTLY if ignored (see docs/plans/planetary-native-video.md Phase E):
    //   1. The zoom factor is a THRESHOLD, not a value: 1-4 give 1x, 5-8 give 5x, 10 and up give 10x. So a
    //      requested window size selects a level, and the body's own rect is the only truth about scale.
    //   2. Evf_AFMode = LiveFace disables magnification on a body with a lens attached and ACKs the zoom
    //      anyway, so the AF method is set to Live before asking.
    //   3. Factor is 4.96 for a nominal 5x, because the crop is a whole number of pixels. Planetary pixel
    //      scale must come from the rect, never from the level requested.
    //   4. A zoom takes about a second to apply while the body keeps streaming PRE-zoom frames, so a rect
    //      read straight after the call reports the OLD crop. Hence verify: true at every level change,
    //      which waits for the crop to actually move.
    //
    // EVF exposure is still EVF-auto rather than a true integration time (ISO/gain tuning works through
    // ApplyVideoControlsAsync), and streaming stays mutually exclusive with single-shot capture.

    /// <summary>EVF poll cadence floor -- the feed runs at its own fps; we treat the requested exposure as a
    /// poll interval clamped to this range so a large "exposure" can't stall the feed to one frame per minute.</summary>
    private static readonly TimeSpan MinVideoPace = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan MaxVideoPace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The live-view crop the body last confirmed, in the body's OWN sensor coordinate space, plus whether it
    /// can be panned. Held as a record so the whole snapshot swaps in one reference write: the rect alone is
    /// 16 bytes, well past a pointer, and <see cref="VideoRoi"/> / <see cref="CanJogRoi"/> are polled from the
    /// render thread while the capture loop writes them. <see langword="null"/> means no stream is running.
    /// </summary>
    /// <param name="Roi">The magnified region in sensor px, as the body reports it.</param>
    /// <param name="SensorWidth">The body's own full-frame width, which is what bounds the pan range. Taken
    /// from the frame record rather than <see cref="CameraXSize"/>, because the body clamps a pan in the space
    /// it reported and the two need not agree (active area against total).</param>
    /// <param name="SensorHeight">The body's own full-frame height.</param>
    /// <param name="CanPan">Magnified AND the body advertises the pan operation. False at 1x, where the crop is
    /// the whole frame and the accepted range collapses to a single point.</param>
    internal sealed record EvfWindow(RoiRect Roi, int SensorWidth, int SensorHeight, bool CanPan);

    private EvfWindow? _evfWindow;

    /// <inheritdoc/>
    public bool CanVideoCapture => Connected;

    /// <inheritdoc/>
    // True only while the feed is a magnified, pannable crop. At 1x there is nowhere to pan to, so the
    // recenter loop falls back to the mount on its own without needing to know why.
    public bool CanJogRoi => Volatile.Read(ref _evfWindow) is { CanPan: true };

    /// <inheritdoc/>
    public int DroppedFrames => 0; // EVF has no drop counter.

    /// <inheritdoc/>
    // The magnified crop in SENSOR px, which is the space the pan range and JogRoiAsync are measured in.
    // Deliberately not the size of the yielded frame: the EVF renders a 1104x736 crop as a ~1024x680 JPEG, so
    // one frame px is ~1.08 sensor px at 5x. The recenter controller measures its offset in frame px and
    // applies it as sensor px, so it under-corrects by that ratio, which a damped loop absorbs as a slightly
    // lower gain. Reporting frame px instead would break the pan-range arithmetic (sensorWidth - roi.Width),
    // which is the part the loop cannot be allowed to get wrong.
    public RoiRect VideoRoi =>
        Volatile.Read(ref _evfWindow)?.Roi ?? new RoiRect(0, 0, CameraXSize, CameraYSize);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Image> CaptureVideoAsync(
        VideoCaptureOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_camera is not { } camera || !Connected)
        {
            throw new InvalidOperationException("Camera is not connected");
        }

        if (Interlocked.CompareExchange(ref _videoActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("A video capture is already running on this camera.");
        }

        try
        {
            if (options.Gain is { } gain)
            {
                await SetGainAsync(gain, cancellationToken);
            }

            var startErr = await camera.StartLiveViewAsync(cancellationToken);
            if (startErr is not EdsError.OK)
            {
                throw new CanonDriverException(startErr, "Failed to start Canon Live View");
            }

            // The readout-window SIZE comes from NumX/NumY, per IVideoCameraDriver. An EOS offers three
            // discrete crops rather than a free rectangle, so the request snaps to the nearest zoom level.
            // Applied even when that is 1x, because zoom and pan PERSIST on the body: a stream that inherited
            // a magnified crop from an earlier session would otherwise start on a corner of the sensor.
            var zoom = ZoomForWindow(NumX, CameraXSize);
            await ApplyEvfZoomAsync(camera, zoom, cancellationToken);

            // Requested exposure as a poll-cadence floor (EVF has no true integration time), clamped so a huge
            // value can't stall the feed. Live-tunable exposure is not modelled on EVF; ISO is (ApplyVideoControls).
            var pace = options.Exposure <= TimeSpan.Zero ? MinVideoPace
                : options.Exposure < MinVideoPace ? MinVideoPace
                : options.Exposure > MaxVideoPace ? MaxVideoPace
                : options.Exposure;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Disconnect out from under an active stream (app shutdown) is a stop signal too.
                if (!Connected)
                {
                    yield break;
                }

                // Live-resizable, like every other streaming driver: re-read the requested window each pass
                // and re-zoom when it now maps to a different level. ApplyEvfZoomAsync is total, so a
                // cancellation here falls through to the token check that ends the loop.
                var wanted = ZoomForWindow(NumX, CameraXSize);
                if (wanted != zoom)
                {
                    zoom = wanted;
                    await ApplyEvfZoomAsync(camera, zoom, cancellationToken);
                }

                // Fetch the next EVF JPEG. The await carries no yield, so its OCE is caught here and turned
                // into a clean stop (yield return / yield break inside a try/catch is a compile error).
                EdsError err = EdsError.OK;
                byte[] jpeg = [];
                var cancelled = false;
                try
                {
                    (err, jpeg) = await camera.GetLiveViewFrameAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                if (cancelled)
                {
                    yield break;
                }

                if (err is not EdsError.OK || jpeg.Length == 0)
                {
                    // EVF frame not ready yet (ObjectNotReady / DeviceBusy): brief back-off, keep streaming.
                    if (await PaceAsync(MinVideoPace, cancellationToken))
                    {
                        yield break;
                    }
                    continue;
                }

                if (!Image.TryDecodeRaster(jpeg, out var frame))
                {
                    Logger.LogDebug("Canon EVF JPEG frame ({Bytes} bytes) failed to decode", jpeg.Length);
                    if (await PaceAsync(MinVideoPace, cancellationToken))
                    {
                        yield break;
                    }
                    continue;
                }

                yield return frame;

                if (await PaceAsync(pace, cancellationToken))
                {
                    yield break;
                }
            }
        }
        finally
        {
            // Best-effort stop on CancellationToken.None so the EVF is always torn down even on a cancelled stream.
            try
            {
                await camera.StopLiveViewAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Canon Live View stop failed");
            }
            // No stream, no window: CanJogRoi goes false and VideoRoi reverts to the full-frame default rather
            // than reporting the last crop as though it were still live.
            Volatile.Write(ref _evfWindow, null);
            Interlocked.Exchange(ref _videoActive, 0);
        }
    }

    /// <summary>Sleeps the poll interval; returns true if the wait was cancelled (the stream should stop).</summary>
    private async ValueTask<bool> PaceAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await TimeProvider.SleepAsync(interval, cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <inheritdoc/>
    public async ValueTask JogRoiAsync(int dxPixels, int dyPixels, CancellationToken cancellationToken = default)
    {
        if (_camera is not { } camera || Volatile.Read(ref _evfWindow) is not { CanPan: true } window)
        {
            throw new InvalidOperationException(
                "Canon Live View ROI jog needs a magnified, pannable EVF crop. CanJogRoi reports when that "
                + "holds; at 1x the crop is the whole frame and the recenter loop uses mount jog instead.");
        }

        var (x, y) = ClampPan(window, dxPixels, dyPixels);
        if (x == window.Roi.X && y == window.Roi.Y)
        {
            return; // Already against that edge; nothing to send.
        }

        // verify: false deliberately. The verify path polls live-view frames for up to a second to watch the
        // move land, and this runs on the capture loop between frames, where a second of stall is the whole
        // point of not doing it. Since the coordinate was clamped into the range the body accepts, the
        // position it adopts is the one asked for, so the window is updated from the request.
        var (err, _) = await camera.SetEvfZoomPositionAsync((uint)x, (uint)y, verify: false, cancellationToken);
        if (err is not EdsError.OK)
        {
            Logger.LogDebug("Canon EVF pan to ({X},{Y}) failed: {Error}", x, y, err);
            return;
        }

        Volatile.Write(ref _evfWindow, window with { Roi = window.Roi with { X = x, Y = y } });
    }

    /// <summary>
    /// Puts the feed at <paramref name="zoom"/> and publishes the crop the body actually adopted.
    /// </summary>
    /// <remarks>
    /// Verifies, because the zoom operation ACKs unconditionally and the body then streams pre-zoom frames for
    /// about a second, so an unverified call followed by a rect read reports the PREVIOUS crop. Best-effort
    /// throughout: a body that refuses to magnify keeps streaming whatever it is showing, and the window
    /// published from its own rect then simply reports no pan range.
    /// </remarks>
    private async Task ApplyEvfZoomAsync(CanonCamera camera, CanonEvfZoom zoom, CancellationToken cancellationToken)
    {
        try
        {
            // The AF method gates magnification: LiveFace refuses to magnify on a body with a lens attached
            // and ACKs the zoom regardless, so switch to the method that works first. Only when actually
            // magnifying, because this WRITES a camera setting the user can see in the body's own menus, and
            // plain full-frame streaming has no business changing it. Best-effort: a body that rejects the
            // write just stays where it is, and the zoom verify below is what reports the consequence.
            if (zoom is not CanonEvfZoom.Fit)
            {
                var afErr = await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live, cancellationToken);
                if (afErr is not EdsError.OK)
                {
                    Logger.LogDebug(
                        "Canon EVF AF method could not be set to Live ({Error}); magnification may be refused",
                        afErr);
                }
            }

            var err = await camera.SetEvfZoomAsync(zoom, verify: true, cancellationToken);
            if (err is not EdsError.OK)
            {
                Logger.LogWarning(
                    "Canon EVF zoom {Zoom} was not adopted ({Error}); staying at the current magnification",
                    zoom, err);
            }

            // Read the rect whatever the zoom answered: it is the only account of where the crop sits and how
            // big it is, which is what the recenter loop pans, and it is right even when the zoom was refused.
            PublishEvfWindow(camera, await camera.GetEvfZoomRectAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // The stream is shutting down; the caller's own token check ends the enumeration.
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Canon EVF zoom {Zoom} failed", zoom);
        }
    }

    /// <summary>
    /// Records the body's reported crop as the live ROI. A rect the body did not describe publishes a
    /// full-frame window with no pan range, which is the honest answer: unknown geometry must never read as
    /// pannable, or the recenter loop would jog against a window it cannot place.
    /// </summary>
    private void PublishEvfWindow(CanonCamera camera, CanonEvfZoomRect? rect)
    {
        var window = WindowFor(rect, camera.SupportsEvfZoomPosition, CameraXSize, CameraYSize);
        Volatile.Write(ref _evfWindow, window);
        Logger.LogDebug("Canon EVF window {Roi} of {W}x{H}, pannable {CanPan}",
            window.Roi, window.SensorWidth, window.SensorHeight, window.CanPan);
    }

    /// <summary>
    /// The window a reported zoom rect describes, or a non-pannable full-frame window when the body did not
    /// describe one. Pure, so the rule that matters can be pinned: a rect we cannot place must never come back
    /// pannable.
    /// </summary>
    /// <param name="rect">What the body reported, or <see langword="null"/> when it reported nothing.</param>
    /// <param name="bodySupportsPan">Whether the body advertises the pan operation at all (0x9159).</param>
    /// <param name="fallbackWidth">Sensor width to fall back on when the rect is absent or unusable.</param>
    /// <param name="fallbackHeight">Sensor height to fall back on.</param>
    internal static EvfWindow WindowFor(
        CanonEvfZoomRect? rect, bool bodySupportsPan, int fallbackWidth, int fallbackHeight)
    {
        // A rect with no sensor bounds cannot answer "is this magnified" or "how far can it pan", and a zero
        // crop is not a window at all, so both fall through to the full-frame default rather than being
        // half-trusted.
        if (rect is { SensorWidth: > 0, SensorHeight: > 0, Width: > 0, Height: > 0 } r)
        {
            return new EvfWindow(
                new RoiRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height),
                (int)r.SensorWidth,
                (int)r.SensorHeight,
                r.IsMagnified && bodySupportsPan);
        }

        return new EvfWindow(
            new RoiRect(0, 0, fallbackWidth, fallbackHeight), fallbackWidth, fallbackHeight, false);
    }

    /// <summary>
    /// Where a pan of (<paramref name="dxPixels"/>, <paramref name="dyPixels"/>) from
    /// <paramref name="window"/> may actually land.
    /// </summary>
    /// <remarks>
    /// Clamped here rather than left to the body. A coordinate up to (sensor - crop) is accepted and then
    /// clamped inwards by the body itself, but anything BEYOND that is discarded outright and the axis silently
    /// keeps its previous value, which is indistinguishable from a pan that did not work. So asking for "the
    /// far corner" with a large number moves nothing at all, and the far corner is computed instead.
    /// </remarks>
    internal static (int X, int Y) ClampPan(EvfWindow window, int dxPixels, int dyPixels)
    {
        var maxX = Math.Max(0, window.SensorWidth - window.Roi.Width);
        var maxY = Math.Max(0, window.SensorHeight - window.Roi.Height);
        return (
            Math.Clamp(window.Roi.X + dxPixels, 0, maxX),
            Math.Clamp(window.Roi.Y + dyPixels, 0, maxY));
    }

    /// <summary>
    /// The zoom level whose crop is closest to a requested window width.
    /// </summary>
    /// <remarks>
    /// An EOS offers three discrete magnifications rather than a free rectangle, so a requested size snaps to
    /// one and the body's own rect is then the truth about what that means in pixels (a nominal 5x measures
    /// 4.96x). Thresholds sit between the nominal factors, so a full-frame request gives 1x and a request for
    /// a tenth gives 10x. A width of 0, the default before anything sets NumX, is a full-frame request.
    /// </remarks>
    internal static CanonEvfZoom ZoomForWindow(int requestedWidth, int sensorWidth)
    {
        if (requestedWidth <= 0 || sensorWidth <= 0 || requestedWidth >= sensorWidth)
        {
            return CanonEvfZoom.Fit;
        }

        var factor = (double)sensorWidth / requestedWidth;
        return factor < 3.0 ? CanonEvfZoom.Fit
            : factor < 7.5 ? CanonEvfZoom.X5
            : CanonEvfZoom.X10;
    }

    /// <inheritdoc/>
    public async ValueTask ApplyVideoControlsAsync(VideoCaptureOptions controls, CancellationToken cancellationToken = default)
    {
        // Live-tune the running stream. ISO (gain) is a real EVF control; exposure on EVF is auto (not a true
        // integration time), so it is intentionally not applied -- see the region banner. No-op gain when null.
        if (controls.Gain is { } gain)
        {
            await SetGainAsync(gain, cancellationToken);
        }
    }

    // --- IDisposable ---
    public void Dispose() { }

    public async ValueTask DisposeAsync()
    {
        if (_downloadTask is not null)
        {
            try { await _downloadTask; } catch { /* swallow */ }
        }
        await DisconnectAsync();
    }
}
