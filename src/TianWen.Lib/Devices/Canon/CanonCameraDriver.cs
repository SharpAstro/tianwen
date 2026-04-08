using FC.SDK;
using FC.SDK.Canon;
using FC.SDK.Transport;
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
internal sealed class CanonCameraDriver : ICameraDriver
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
    private double _pixelSizeX = 6.55; // default: 6D
    private double _pixelSizeY = 6.55;
    private int _cameraXSize = 5472;
    private int _cameraYSize = 3648;

    public CanonCameraDriver(CanonDevice device, IExternal external, CanonCameraFactory cameraFactory)
    {
        _device = device;
        _external = external;
        _cameraFactory = cameraFactory;
    }

    // --- IDeviceDriver ---
    public string Name => _device.DisplayName;
    public string? Description => _device.IsWifi ? "Canon DSLR (WiFi/PTP-IP)" : "Canon DSLR (USB)";
    public string? DriverInfo => Description;
    public string? DriverVersion => "1.0";
    public DeviceType DriverType => DeviceType.Camera;
    public IExternal External => _external;
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
            _external.AppLogger.LogDebug(ex, "Could not read current ISO from Canon camera");
        }

        // Enable mirror lockup for astrophotography (reduces vibration during exposures)
        try
        {
            var (mluErr, mluSetting) = await _camera.GetMirrorUpSettingAsync(cancellationToken);
            if (mluErr is EdsError.OK && mluSetting is EdsMirrorUpSetting.Off)
            {
                var enableResult = await _camera.EnableMirrorLockupAsync(cancellationToken);
                if (enableResult is EdsError.OK)
                {
                    _external.AppLogger.LogInformation("Mirror lockup enabled automatically for astrophotography");
                }
            }
        }
        catch (Exception ex)
        {
            _external.AppLogger.LogDebug(ex, "Could not configure mirror lockup on Canon camera");
        }

        Volatile.Write(ref _connected, true);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        _external.AppLogger.LogInformation("Canon camera connected: {Name} ({Transport})", Name, _device.IsWifi ? "WiFi" : "USB");
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
            _external.AppLogger.LogInformation("Canon mirror lockup {State}", value ? "enabled" : "disabled");
        }
        else
        {
            _external.AppLogger.LogWarning("Failed to {Action} Canon mirror lockup: {Error}", value ? "enable" : "disable", result);
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

        var startTime = _external.TimeProvider.GetUtcNow();
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
            await _external.SleepAsync(duration, cancellationToken);
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

                _external.AppLogger.LogDebug("Canon image downloaded: {W}x{H}", image.Width, image.Height);
            }
            else
            {
                _external.AppLogger.LogError("Failed to decode CR2 from Canon camera");
            }
        }
        catch (Exception ex)
        {
            _external.AppLogger.LogError(ex, "Canon image download failed");
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
