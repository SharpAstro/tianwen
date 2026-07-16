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
/// Images are downloaded as CR2 and decoded via Magick.NET.
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

        // Apply astrophotography-friendly defaults. Each setter is best-effort — older
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
    public Filter Filter { get; set; }
    public int FocusPosition { get; set; }
    public Target? Target { get; set; }

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
    /// Reads the C.Fn block, flips Long Exposure NR to Off using whichever of the
    /// known per-generation function IDs (6D-class or Rebel-class) is present on this
    /// body, and writes it back. Silent no-op if the ID isn't found or the camera
    /// doesn't expose a C.Fn block.
    /// </summary>
    private async ValueTask DisableLongExposureNRAsync(CancellationToken ct)
    {
        if (_camera is null)
        {
            return;
        }

        try
        {
            var (err, block) = await _camera.GetCustomFunctionBlockAsync(ct);
            if (err is not EdsError.OK || block is null)
            {
                Logger.LogDebug("Canon LongExposureNR: C.Fn block read failed ({Error})", err);
                return;
            }

            var offValue = (uint)EdsLongExposureNR.Off;
            var patched = block.SetValue(CanonCustomFunctionId.LongExposureNR_6D, offValue)
                       || block.SetValue(CanonCustomFunctionId.LongExposureNR_Rebel, offValue);
            if (!patched)
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
    // Core (this build): full-frame framing / EAA streaming, mutually exclusive with single-shot capture.
    // Deferred (needs an FC.SDK point/rect property accessor): the 5x/10x EVF-zoom planetary regime + its
    // pannable zoom crop as the host-side ROI jog. Evf_Zoom (the magnification level) is a plain uint and is
    // reachable today, but Evf_ZoomPosition (0x508) / Evf_ZoomRect (0x541) are POINT/RECT properties (8+ bytes)
    // and FC.SDK only exposes a uint32 property accessor -- so CanJogRoi is false here and the recenter loop
    // falls back to mount jog (PlanetaryRecenterController already degrades cleanly). EVF exposure is also
    // EVF-auto, not a true integration time (ISO/gain tuning still works via ApplyVideoControlsAsync).

    /// <summary>EVF poll cadence floor -- the feed runs at its own fps; we treat the requested exposure as a
    /// poll interval clamped to this range so a large "exposure" can't stall the feed to one frame per minute.</summary>
    private static readonly TimeSpan MinVideoPace = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan MaxVideoPace = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc/>
    public bool CanVideoCapture => Connected;

    /// <inheritdoc/>
    // Canon EVF has no host-side ROI pan through the currently-published FC.SDK (see the region banner);
    // the recenter loop falls back to mount jog. Promote to true once EVF-zoom-pan lands.
    public bool CanJogRoi => false;

    /// <inheritdoc/>
    public int DroppedFrames => 0; // EVF has no drop counter.

    /// <inheritdoc/>
    // Full-frame window: without an FC.SDK zoom-rect read we can't report a magnified EVF crop's origin/size,
    // and CanJogRoi is false so the recenter loop never reads this for panning. Sensor-sized default.
    public RoiRect VideoRoi => new(0, 0, CameraXSize, CameraYSize);

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
    public ValueTask JogRoiAsync(int dxPixels, int dyPixels, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Canon Live View does not support host-side ROI jog (EVF zoom pan needs an FC.SDK point-property "
            + "accessor); CanJogRoi is false and the recenter loop uses mount jog instead.");

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
