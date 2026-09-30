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
/// Uses <see cref="CanonCamera.TakePictureWithMirrorLockupAsync"/> (or <see cref="CanonCamera.TakePictureAsync"/> with the
/// device's Mirror lockup setting off) for exposures ≤30s (Tv mode)
/// and <see cref="CanonCamera.BulbStartAsync"/>/<see cref="CanonCamera.BulbEndAsync"/> for longer exposures.
/// Images are downloaded as CR2 and decoded via the SharpAstro codecs facade (FC.SDK.Raw).
/// </summary>
internal sealed class CanonCameraDriver : ICameraDriver, IVideoCameraDriver
{
    // Shutter speeds are not a table here: the body says which Tv codes it offers (a third-stop and a half-stop body offer
    // different ones, 10 s being 0x1D in thirds and 0x1C in halves, and the Exposure level increments C.Fn switches one body
    // between them), the code says its own duration (APEX, see TvDuration), and the frame's EXIF says what it was taken at. A
    // 6D answers DeviceBusy, not "invalid", to a code it does not offer: a hand-kept table asked it for the half-stop 10 s and
    // the exposure failed as a busy body, and four of that table's durations were wrong.

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
    private readonly CanonBodyRegistry? _bodies;
    private CanonCamera? _camera;
    private bool _connected;
    private bool _bulbActive;

    // Every object the body announces goes through one queue and one reader (PumpAnnouncedObjectsAsync), which downloads the
    // raw an exposure is owed and releases (TransferComplete) everything else. A host-destination frame the body is not
    // released from stays in its RAM, and while it holds one it refuses property writes, and with enough of them releases.
    // One handle per exposure used to be taken and any other dropped: the JPEG of a RAW+JPEG body, and a raw that arrived
    // after its exposure had been given up (FC.SDK counted 8 frames held after 8 downloads).
    private System.Threading.Channels.Channel<uint> _announced = NewAnnouncedQueue();
    private Task? _objectPump;
    private CancellationTokenSource? _pumpStop;
    private Task? _decodeTask;

    // The exposure (its generation) a raw is still owed to, 0 when none is.
    private int _awaitingRaw;

    // Until when the body is still taking a shot it was released for, 0 once any object has come since: a shot given up or
    // aborted goes on (a Tv exposure cannot be stopped once released), and the body answers DeviceBusy to every write until
    // it is done, which read as a body that had stopped (WaitForTheBodyAsync).
    private long _bodyBusyUntilTicks;

    // The latest exposure the body sent a JPEG for while its raw was owed, so a lost exposure can say it got only a JPEG.
    private int _jpegGeneration;
    private int _jpegNoted;

    // Mirror lockup is taken per exposure through the body's own 2 s self-timer, never left armed (ReleaseShutterAsync), and
    // a lockup capture's restore of the drive waits for its frame, so the release and that restore take turns.
    private bool _mirrorLockup;
    private bool _mirrorLockupUnavailable;
    private readonly SemaphoreSlim _releaseGate = new SemaphoreSlim(1, 1);

    // A picture the body never delivers throws nothing, so the wait for one needs an end of its own (DAL does the same).
    // The deadline is checked where the imaging loop polls, so it is testable on a fake clock and needs no timer.
    private static readonly TimeSpan LostExposureGrace = TimeSpan.FromSeconds(30);
    private long _exposureDeadlineTicks;
    private string? _exposureFault;
    private int _exposureGeneration;
    private int _whiteAdu;

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
        _bodies = serviceProvider.GetService<CanonBodyRegistry>();
        _mirrorLockup = device.MirrorLockup;
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
            _camera = _cameraFactory.ConnectWpd(_device.WpdDeviceId);
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
            var deviceId = _device.RawDeviceId;
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

        // The queue's reader is running before the body can announce anything.
        _announced = NewAnnouncedQueue();
        _pumpStop = new CancellationTokenSource();
        var pumpCamera = _camera;
        var pumpReader = _announced.Reader;
        var pumpToken = _pumpStop.Token;
        _objectPump = Task.Run(() => PumpAnnouncedObjectsAsync(pumpCamera, pumpReader, pumpToken), CancellationToken.None);

        _camera.StartEventPolling();
        _camera.ObjectAdded += OnObjectAdded;

        // Its session already reports the body's serial: tell discovery, which then keys this camera by it, and never opens
        // a camera this driver is holding to ask (#1097).
        if (_device.IsWpd)
        {
            _bodies?.Remember(_device.WpdDeviceId, _camera.SerialNumber ?? "");
        }

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
        // WhiteBalance=Daylight (the device's White balance setting): a frame carries the as-shot white balance in its
        //                     pixels (CanonRaw.PreprocessMosaic), so under Auto every frame, a dark included, is scaled per
        //                     colour by whatever the body chose for it
        // First, let go of a half press an earlier session left held: the body answers DeviceBusy to every write while one is,
        // closing the session does not let go of it, and until now only a power cycle did. A refused bulb start left one held
        // (FC.SDK let go of it only from 3.2's successor), and so did a release whose transport failed mid-press; letting go
        // with nothing held is answered OK. Measured 2026-09-30: ISO refused busy, let go, ISO taken.
        await TrySetAsync(
            () => _camera.ReleaseShutterAsync(cancellationToken),
            "shutter button let go of", cancellationToken, askAgainWhileBusy: false);
        await TrySetAsync(
            () => _camera.SetSaveToAsync(EdsSaveTo.Host, cancellationToken),
            "SaveTo=Host", cancellationToken);
        // Asked once: a 6D answers DeviceBusy to this on every connect, a power-cycled body's included (2026-09-30), so asking
        // again only held the connect for 9 s.
        await TrySetAsync(
            () => _camera.SetAutoPowerOffAsync(0, cancellationToken),
            "AutoPowerOff=disabled", cancellationToken, askAgainWhileBusy: false);
        await TrySetAsync(
            () => _camera.SetAFModeAsync(EdsAFMode.ManualFocus, cancellationToken),
            "AFMode=ManualFocus", cancellationToken);
        await TrySetAsync(
            () => _camera.SetHighIsoNRAsync(EdsHighIsoNR.Disable, cancellationToken),
            "HighIsoNR=Disable", cancellationToken);
        if (_device.DaylightWhiteBalance)
        {
            await TrySetAsync(
                () => _camera.SetWhiteBalanceAsync(EdsWhiteBalance.Daylight, cancellationToken),
                "WhiteBalance=Daylight", cancellationToken);
        }

        // Long-exposure NR lives in Custom Functions on Canon DSLRs, not as a direct
        // PTP property. Leaving it on doubles every sub (in-camera dark subtraction);
        // proper calibration frames give better results anyway.
        await DisableLongExposureNRAsync(cancellationToken);

        // Mirror lockup is never left armed on the body. Armed on a 6D in a single-shot drive, a release only RAISES the
        // mirror: the body waits for a second press and drops the mirror again after 30 s, so a plain release gets no
        // picture at all (a power-cycled 6D armed here at connect took none on 2026-09-30), and nor does a bulb start. It is
        // taken per exposure instead, where the body can do it (ReleaseShutterAsync).
        await DisarmMirrorLockupAsync(_camera, cancellationToken);

        Volatile.Write(ref _connected, true);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        Logger.LogInformation("Canon camera connected: {Name} ({Transport})", Name, _device.IsWifi ? "WiFi" : "USB");
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_camera is { } camera)
        {
            // A shot given up or aborted is still being taken: let it finish, up to its deadline, so its frame is released and
            // the drive a lockup capture changed is put back, rather than closing on a body left on its self-timer (a run
            // cancelled 8 s into a 30 s dark closed that way, 2026-09-30).
            try
            {
                await WaitForTheBodyAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Canon disconnect stopped waiting for the exposure the body is still taking");
            }

            camera.ObjectAdded -= OnObjectAdded;
            await camera.StopEventPollingAsync();

            // Release what the body announced and nobody took (no raw is owed any more), then stop the queue's reader.
            Volatile.Write(ref _awaitingRaw, 0);
            _announced.Writer.TryComplete();
            if (_objectPump is { } pump && _pumpStop is { } stop)
            {
                stop.CancelAfter(PumpDrainBudget);
                try
                {
                    await pump;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Canon object queue ended with an error");
                }
                stop.Dispose();
            }
            _objectPump = null;
            _pumpStop = null;

            // Leave the body on the drive it was found in: a lockup capture's restore waits for its frame to be released.
            try
            {
                await _releaseGate.WaitAsync(cancellationToken);
                try
                {
                    await ApplyPendingMirrorLockupRestoreAsync(camera, persist: true, cancellationToken);
                }
                finally
                {
                    _releaseGate.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Canon drive restore on disconnect failed");
            }

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

    /// <summary>False; this camera has no guide port at all.</summary>
    public bool CanPulseGuideSimultaneously => false;
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
    // The white point of the latest frame (its body's saturation level times the lowest white-balance factor, see
    // CanonWhitePoint), which is what the pixels are clipped at and so what they are a fraction of. 16383 until a frame says.
    public int MaxADU => Volatile.Read(ref _whiteAdu) is > 0 and var white ? white : 16383;
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

        await WaitForTheBodyAsync(cancellationToken);

        var result = await CanonBusyRetry.RunAsync(
            () => _camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, IsoTable[value].Code, cancellationToken), TimeProvider, cancellationToken);
        if (result is EdsError.OK)
        {
            _currentIsoIndex = value;
        }
        else if (result is EdsError.DeviceBusy)
        {
            throw new InvalidOperationException(BusyMessage($"the {IsoTable[value].Label} setting"));
        }
    }

    private string BusyMessage(string refused)
        => $"{Name} is busy and refused {refused}. If its LCD shows a blinking Err, the body itself has stopped: switch it off and on (or take its battery out).";

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
    // The per-exposure choice (the device's Mirror lockup setting), never the body's own setting, which the driver keeps
    // disarmed: armed, a release only raises the mirror (see ConnectAsync).
    private bool UseMirrorLockup => _mirrorLockup && !_mirrorLockupUnavailable && _camera is { SupportsMirrorLockupCapture: true };

    public ValueTask<bool> GetMirrorLockupAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(UseMirrorLockup);
    }

    public ValueTask SetMirrorLockupAsync(bool value, CancellationToken cancellationToken = default)
    {
        _mirrorLockup = value;
        Logger.LogInformation("Canon mirror lockup per exposure {State}", value ? "on" : "off");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Says in the log how the body was found (its drive and its own mirror lockup setting) and disarms that setting when it is
    /// armed: a release on an armed body only raises the mirror. Lockup is the driver's to take per exposure.
    /// </summary>
    private async ValueTask DisarmMirrorLockupAsync(CanonCamera camera, CancellationToken ct)
    {
        try
        {
            var (driveErr, drive) = await camera.GetDriveModeAsync(ct);
            var (lockupErr, lockup) = await camera.GetMirrorUpSettingAsync(ct);
            Logger.LogInformation(
                "Canon {Name}: drive {Drive}, mirror lockup {Lockup} on the body; each exposure {PerExposure}",
                Name,
                driveErr is EdsError.OK ? drive.ToString() : $"unread ({driveErr})",
                lockupErr is EdsError.OK ? lockup.ToString() : $"unread ({lockupErr})",
                UseMirrorLockup ? "locks the mirror up first (the body's 2 s self-timer)" : "is released without mirror lockup");

            if (_mirrorLockup && !camera.SupportsMirrorLockupCapture)
            {
                Logger.LogWarning(
                    "{Name} keeps mirror lockup as a Custom Function and discards every remote release while it is armed: its exposures are taken without it",
                    Name);
            }

            // A lockup capture that could not put the drive back (the session closed while the body still held its frame) left
            // the self-timer on, which would delay every plain release and survive into the body's own use.
            if (driveErr is EdsError.OK && drive is EdsDriveMode.Timer_2sec or EdsDriveMode.Timer_10sec or EdsDriveMode.Timer_10sec_RemoteControl)
            {
                var single = await CanonBusyRetry.RunAsync(() => camera.SetDriveModeAsync(EdsDriveMode.SingleShooting, ct), TimeProvider, ct);
                Logger.LogInformation("Canon drive {Drive} set back to single shot: {Result}", drive, single);
            }

            if (lockupErr is EdsError.OK && lockup is EdsMirrorUpSetting.On)
            {
                var off = await CanonBusyRetry.RunAsync(() => camera.DisableMirrorLockupAsync(ct), TimeProvider, ct);
                if (off is EdsError.OK)
                {
                    Logger.LogInformation("Canon mirror lockup disarmed on the body: armed, a release would only raise the mirror");
                }
                else
                {
                    Logger.LogWarning("Canon mirror lockup is armed on the body and could not be disarmed ({Error}): an exposure may get no picture", off);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read or disarm mirror lockup on {Name}", Name);
        }
    }

    // --- Pulse guiding (not supported) ---
    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

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
    {
        // The picture is not coming: say so to whoever is waiting for it, every time they ask, until the next exposure.
        if (Volatile.Read(ref _exposureFault) is { } fault)
        {
            throw new InvalidOperationException(fault);
        }

        var state = (CameraState)Volatile.Read(ref _cameraState);
        if (state == CameraState.Idle && _lastImageData is not null)
        {
            return ValueTask.FromResult(true);
        }

        if (state is CameraState.Exposing or CameraState.Download
            && TimeProvider.GetUtcNow().UtcTicks > Volatile.Read(ref _exposureDeadlineTicks))
        {
            var seconds = (_lastExposureDuration ?? TimeSpan.Zero).TotalSeconds;
            throw new InvalidOperationException(AbandonLostExposure(
                Volatile.Read(ref _jpegGeneration) == Volatile.Read(ref _exposureGeneration)
                    ? $"{Name} sent only a JPEG for a {seconds:0.###} s exposure, and TianWen needs its raw: set the camera's image quality to RAW (or RAW+JPEG)."
                    : $"{Name} sent no picture for a {seconds:0.###} s exposure. If its LCD shows a blinking Err, the body itself has stopped: switch it off and on (or take its battery out)."));
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>How long after it began an exposure of <paramref name="duration"/> may take to deliver its picture.</summary>
    internal static TimeSpan LostExposureDeadline(TimeSpan duration) => (duration * 1.1) + LostExposureGrace;

    /// <summary>
    /// Gives up the exposure in flight: the wait for its picture is ended, a picture that arrives late is discarded rather
    /// than taken for the next exposure's, and the camera is free again. Returns <paramref name="message"/>.
    /// </summary>
    private string AbandonLostExposure(string message)
    {
        Interlocked.Increment(ref _exposureGeneration);
        Volatile.Write(ref _awaitingRaw, 0);
        Volatile.Write(ref _exposureFault, message);
        Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
        Logger.LogWarning("{Message}", message);
        return message;
    }

    /// <summary>Ends exposure <paramref name="generation"/> as failed, unless a newer one has begun.</summary>
    private void FailExposure(int generation, string message)
    {
        if (Volatile.Read(ref _exposureGeneration) == generation)
        {
            Volatile.Write(ref _exposureFault, message);
        }
    }

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

        await WaitForTheBodyAsync(cancellationToken);

        var startTime = TimeProvider.GetUtcNow();
        _lastExposureStartTime = startTime;
        _lastExposureDuration = duration;
        _lastExposureFrameType = frameType;
        _lastImageData = null;
        Volatile.Write(ref _exposureFault, null);
        var generation = Interlocked.Increment(ref _exposureGeneration);

        Interlocked.Exchange(ref _cameraState, (int)CameraState.Exposing);

        var bulb = duration > TimeSpan.FromSeconds(30);
        await _releaseGate.WaitAsync(cancellationToken);
        try
        {
            // Back to the drive the body was found in before a release: a lockup capture leaves that until its frame is
            // released, and a release on the self-timer drive with lockup armed would be a different exposure.
            await ApplyPendingMirrorLockupRestoreAsync(_camera, persist: true, cancellationToken);

            if (!bulb)
            {
                // Tv mode: set shutter speed then take picture. The speed is one the body offers, and the frame says the one
                // it was taken at.
                var tvCode = ClosestTv(duration, await _camera.GetAllowedValuesAsync(EdsPropertyId.Tv, cancellationToken));
                _lastExposureDuration = TvDuration(tvCode);
                var tvResult = await CanonBusyRetry.RunAsync(
                    () => _camera.SetPropertyAsync(EdsPropertyId.Tv, tvCode, cancellationToken), TimeProvider, cancellationToken);
                if (tvResult is EdsError.DeviceBusy)
                {
                    Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
                    throw new InvalidOperationException(BusyMessage("the shutter speed"));
                }

                Volatile.Write(ref _exposureDeadlineTicks, (startTime + LostExposureDeadline(duration)).UtcTicks);

                // Owed a raw from here: the object queue downloads an announcement matched to this exposure, and releases the rest.
                Volatile.Write(ref _awaitingRaw, generation);
                var released = await ReleaseShutterAsync(_camera, cancellationToken);
                if (released is EdsError.OK)
                {
                    Volatile.Write(ref _bodyBusyUntilTicks, (startTime + LostExposureDeadline(TvDuration(tvCode))).UtcTicks);
                }
                else
                {
                    Volatile.Write(ref _awaitingRaw, 0);
                    Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
                    throw new InvalidOperationException(released is EdsError.DeviceBusy
                        ? BusyMessage("the release")
                        : $"{Name} refused the release ({released}).");
                }
            }
            else
            {
                // Bulb takes no mirror lockup: an armed bulb start only raises the mirror, and over 30 s the slap is a small part
                // of the exposure. The body's own setting is disarmed at connect; one still armed here was switched on since.
                if (_camera.MirrorLockupEnabled is true)
                {
                    Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
                    throw new InvalidOperationException(
                        $"{Name} has mirror lockup switched on, and a bulb exposure would only raise the mirror: switch it off in the camera's menu.");
                }

                Volatile.Write(ref _awaitingRaw, generation);
                _bulbActive = true;
                var opened = await _camera.BulbStartAsync(cancellationToken);
                if (opened is not EdsError.OK)
                {
                    _bulbActive = false;
                    Volatile.Write(ref _awaitingRaw, 0);
                    Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
                    throw new InvalidOperationException(opened is EdsError.DeviceBusy
                        ? BusyMessage("the bulb exposure")
                        : $"{Name} refused a bulb exposure ({opened}): an exposure over 30 s needs its mode dial on B.");
                }
            }
        }
        catch
        {
            // A transport that threw before anything was released (or a cancel): the camera is free again and owed no raw,
            // where it was left Exposing for ever.
            Volatile.Write(ref _awaitingRaw, 0);
            Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
            throw;
        }
        finally
        {
            _releaseGate.Release();
        }

        if (bulb)
        {
            await TimeProvider.SleepAsync(duration, cancellationToken);
            await _camera.BulbEndAsync(cancellationToken);
            _bulbActive = false;
            Volatile.Write(ref _exposureDeadlineTicks, (TimeProvider.GetUtcNow() + LostExposureGrace).UtcTicks);
            Volatile.Write(ref _bodyBusyUntilTicks, (TimeProvider.GetUtcNow() + LostExposureGrace).UtcTicks);
        }

        return startTime;
    }

    /// <summary>
    /// Waits, up to its deadline, for the body to finish a shot whose picture has not come: one given up or aborted keeps
    /// exposing and answers DeviceBusy to every write until it is done. A 30 s dark cancelled 14 s in made the next run's ISO
    /// write fail as a busy body; its picture, when it comes, is released by the object queue.
    /// </summary>
    private async ValueTask WaitForTheBodyAsync(CancellationToken ct)
    {
        var said = false;
        while (Volatile.Read(ref _bodyBusyUntilTicks) is var until and > 0 && TimeProvider.GetUtcNow().UtcTicks < until)
        {
            if (!said)
            {
                Logger.LogInformation("Waiting for {Name} to finish the exposure it is still taking", Name);
                said = true;
            }

            await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>
    /// Releases the shutter: with <see cref="UseMirrorLockup"/>, through the body's 2 s self-timer with the mirror locked up
    /// first (the body raises the mirror, lets it settle and exposes on its own, so the settle is its timer's, ~2.9 s from
    /// release to picture on a 6D); otherwise a plain release.
    /// </summary>
    private async ValueTask<EdsError> ReleaseShutterAsync(CanonCamera camera, CancellationToken ct)
    {
        if (UseMirrorLockup)
        {
            var locked = await camera.TakePictureWithMirrorLockupAsync(EdsDriveMode.Timer_2sec, ct);

            // Refused before anything was released: the body offers no self-timer drive to settle with. Take it without lockup
            // from here on, and say so once.
            if (locked is not EdsError.OperationRefused)
            {
                return locked;
            }

            _mirrorLockupUnavailable = true;
            Logger.LogWarning("{Name} cannot take an exposure with mirror lockup (it offers no self-timer drive): exposing without it", Name);
        }

        return await camera.TakePictureAsync(ct);
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
        // A picture that arrives after an abort is the aborted exposure's, not the next one's: the object queue releases it.
        Interlocked.Increment(ref _exposureGeneration);
        Volatile.Write(ref _awaitingRaw, 0);
        Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
    }

    // How long a disconnect lets the object queue release what the body still announced, before it stops it.
    private static readonly TimeSpan PumpDrainBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The queue of announced objects. Its reader must be able to COUNT (the queue asks whether more is waiting before it
    /// restores the drive): a single-reader unbounded channel cannot, and its Count threw after the first object, which left
    /// every later one unread and held in the body.
    /// </summary>
    internal static System.Threading.Channels.Channel<uint> NewAnnouncedQueue()
    {
        return System.Threading.Channels.Channel.CreateUnbounded<uint>();
    }

    private void OnObjectAdded(object? sender, CanonObjectAddedEventArgs e)
    {
        _announced.Writer.TryWrite(e.ObjectHandle);
    }

    /// <summary>What becomes of an object the body announced.</summary>
    internal enum AnnouncedObjectFate
    {
        /// <summary>The raw the exposure in progress is owed: downloaded, then released.</summary>
        Download,

        /// <summary>Not a raw (the JPEG of a RAW+JPEG body): released unread.</summary>
        ReleaseNotRaw,

        /// <summary>A raw no exposure is owed (one given up, or aborted): released unread.</summary>
        ReleaseUnowed,
    }

    /// <summary>
    /// What becomes of an object named <paramref name="fileName"/> (null when the body would not say), with
    /// <paramref name="awaitingRaw"/> the exposure a raw is owed to (0: none) and <paramref name="currentGeneration"/> the
    /// exposure in progress. An object whose name could not be read is taken for the raw when one is owed: the decode says
    /// otherwise, where dropping it would hold the frame in the body.
    /// </summary>
    internal static AnnouncedObjectFate FateOf(string? fileName, int awaitingRaw, int currentGeneration)
    {
        if (fileName is not null && !IsRawFileName(fileName))
        {
            return AnnouncedObjectFate.ReleaseNotRaw;
        }

        return awaitingRaw != 0 && awaitingRaw == currentGeneration ? AnnouncedObjectFate.Download : AnnouncedObjectFate.ReleaseUnowed;
    }

    /// <summary>Whether <paramref name="fileName"/> names a Canon raw (CR2, CR3 or CRW).</summary>
    internal static bool IsRawFileName(string fileName)
    {
        return Path.GetExtension(fileName).ToUpperInvariant() is ".CR2" or ".CR3" or ".CRW";
    }

    /// <summary>
    /// The one reader of the announced objects, for one connection: each is the raw an exposure is owed and downloaded, or is
    /// released. Either way the body is told it may let go of it.
    /// </summary>
    private async Task PumpAnnouncedObjectsAsync(CanonCamera camera, System.Threading.Channels.ChannelReader<uint> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var handle in reader.ReadAllAsync(ct))
            {
                // One object that goes wrong must not end the queue: every object after it would then be held in the body,
                // which is what a queue that stopped reading after its first object did.
                try
                {
                    await TakeOrReleaseAsync(camera, handle, ct);

                    // With nothing more announced, the body takes writes again: put back what a lockup capture changed.
                    if (reader.Count == 0)
                    {
                        await _releaseGate.WaitAsync(ct);
                        try
                        {
                            await ApplyPendingMirrorLockupRestoreAsync(camera, persist: false, ct);
                        }
                        finally
                        {
                            _releaseGate.Release();
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Logger.LogWarning(ex, "Canon object 0x{Handle:X8} could not be handled; the queue goes on", handle);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disconnecting: closing the session releases whatever the body still holds.
            Logger.LogDebug("Canon object queue stopped with {Count} object(s) unread", reader.Count);
        }
    }

    private async Task TakeOrReleaseAsync(CanonCamera camera, uint handle, CancellationToken ct)
    {
        string? fileName = null;
        var nameAnswer = "";
        try
        {
            var (nameErr, name) = await camera.GetObjectFileNameAsync(handle, ct);
            fileName = nameErr is EdsError.OK ? name : null;
            nameAnswer = nameErr is EdsError.OK ? "" : $": {nameErr}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Canon object 0x{Handle:X8}: its name could not be read", handle);
        }

        // Whatever it is, the shot it came from is over.
        Volatile.Write(ref _bodyBusyUntilTicks, 0);

        var owed = Volatile.Read(ref _awaitingRaw);
        var fate = FateOf(fileName, owed, Volatile.Read(ref _exposureGeneration));
        Logger.LogDebug("Canon announced object 0x{Handle:X8} ({FileName}): {Fate}", handle, fileName ?? $"name unread{nameAnswer}", fate);

        if (fate is AnnouncedObjectFate.Download && Interlocked.CompareExchange(ref _awaitingRaw, 0, owed) == owed)
        {
            await DownloadAsync(camera, handle, owed, ct);
            return;
        }

        if (fate is AnnouncedObjectFate.ReleaseNotRaw && owed != 0)
        {
            NoteJpeg(owed, Path.GetExtension(fileName));
        }

        await ReleaseObjectAsync(camera, handle, ct);
    }

    /// <summary>Remembers that exposure <paramref name="generation"/> was sent a JPEG, and says once that the body records one.</summary>
    private void NoteJpeg(int generation, string? extension)
    {
        Volatile.Write(ref _jpegGeneration, generation);
        if (Interlocked.Exchange(ref _jpegNoted, 1) == 0)
        {
            Logger.LogInformation(
                "{Name} also records a {Extension} with each exposure (its image quality includes one): only the raw is kept, the rest is released",
                Name, extension is { Length: > 0 } ? extension : "JPEG");
        }
    }

    /// <summary>
    /// Whether <paramref name="head"/>, a file's first bytes, is a JPEG's (the SOI marker and the next marker's lead byte): what
    /// the body sent when it would not name the object, told apart from a raw by what it is.
    /// </summary>
    internal static bool IsJpeg(ReadOnlySpan<byte> head)
    {
        return head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
    }

    private static bool IsJpegFile(string path)
    {
        Span<byte> head = stackalloc byte[3];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && IsJpeg(head);
    }

    /// <summary>Tells the body it may let go of <paramref name="handle"/>, and says so when it will not.</summary>
    private async Task ReleaseObjectAsync(CanonCamera camera, uint handle, CancellationToken ct)
    {
        try
        {
            // The answer is read: a frame the body is not told it may let go of stays in its RAM, and enough of them stop it
            // releasing at all (FC.SDK's ReleasePendingTransfersAsync documents it). Ignored, that looked like a lost exposure.
            var released = await camera.TransferCompleteAsync(handle, ct);
            if (released is EdsError.OK)
            {
                Logger.LogDebug("Canon TransferComplete for object 0x{Handle:X8}: OK", handle);
            }
            else
            {
                Logger.LogWarning("Canon TransferComplete for object 0x{Handle:X8} answered {Error}: the body keeps the frame", handle, released);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Canon TransferComplete for object 0x{Handle:X8} failed: the body keeps the frame", handle);
        }
    }

    /// <summary>
    /// Downloads exposure <paramref name="generation"/>'s raw and releases it, whether or not the download worked, then decodes
    /// it off the queue, so an object announced meanwhile (the JPEG of RAW+JPEG) is released at once.
    /// </summary>
    private async Task DownloadAsync(CanonCamera camera, uint handle, int generation, CancellationToken ct)
    {
        if (Volatile.Read(ref _exposureGeneration) == generation)
        {
            Interlocked.Exchange(ref _cameraState, (int)CameraState.Download);
        }

        var tmpPath = Path.Combine(Path.GetTempPath(), $"tianwen_canon_{Guid.NewGuid():N}.cr2");
        string? failure = null;
        try
        {
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                var got = await camera.DownloadAsync(handle, fs, ct);
                if (got is not EdsError.OK)
                {
                    failure = $"The picture from {Name} could not be downloaded ({got}).";
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogError(ex, "Canon image download failed");
            failure = $"The picture from {Name} could not be downloaded: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(tmpPath);
            throw;
        }

        await ReleaseObjectAsync(camera, handle, ct);

        if (failure is not null)
        {
            Logger.LogError("{Failure}", failure);
            DeleteQuietly(tmpPath);
            FailExposure(generation, failure);
            IdleIfCurrent(generation);
            return;
        }

        // An object the body would not name, and a JPEG after all (RAW+JPEG): released like one, and the exposure is still owed
        // its raw, which the next announcement is looked at for.
        if (IsJpegFile(tmpPath))
        {
            DeleteQuietly(tmpPath);
            NoteJpeg(generation, null);
            if (Volatile.Read(ref _exposureGeneration) == generation)
            {
                Interlocked.CompareExchange(ref _awaitingRaw, generation, 0);
                Interlocked.CompareExchange(ref _cameraState, (int)CameraState.Exposing, (int)CameraState.Download);
            }
            return;
        }

        _decodeTask = Task.Run(() => DecodeDownloaded(tmpPath, generation), CancellationToken.None);
    }

    private void DecodeDownloaded(string tmpPath, int generation)
    {
        try
        {
            // Into a recycled plane, the DAL pattern: the ref-counted buffer travels ON the channel into
            // GetImageAsync's Image, whose release hands the plane back for the next sub. A new plane per sub
            // was 120 MB on a 30 MP body. (FC.SDK.Raw's own decode buffers are its to recycle.)
            // In ADU counts (the driver's Int16 depth and MaxADU say so), clipped at the body's white point.
            if (Image.TryReadCanonRaw(tmpPath, _stillPlanes.Take, out var image, aduDomain: true))
            {
                if (image.ImageMeta.SensorFullScaleAdu is { } white)
                {
                    Volatile.Write(ref _whiteAdu, (int)MathF.Round(white));
                }

                if (Volatile.Read(ref _exposureGeneration) != generation)
                {
                    // Abandoned while it downloaded: the next exposure owns the camera's state now.
                    return;
                }

                // What the body says it exposed for (EXIF ExposureTime: 10 s for Tv 0x1D, a bulb's own length), before the
                // frame is published, so the frame's EXPTIME is the camera's.
                if (image.ImageMeta.ExposureDuration > TimeSpan.Zero)
                {
                    _lastExposureDuration = image.ImageMeta.ExposureDuration;
                }

                _lastImageData = _stillPlanes.Wrap(image.GetChannelArray(0), image.MinValue, image.MaxValue, 0, Filter.None);

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
                FailExposure(generation, $"{Name} sent a picture that could not be read (a CR2 that did not decode). Take it again; if it keeps failing, switch the camera off and on.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Canon image decode failed");
            FailExposure(generation, $"{Name} sent a picture that could not be read: {ex.Message}");
        }
        finally
        {
            DeleteQuietly(tmpPath);
            IdleIfCurrent(generation);
        }
    }

    /// <summary>The camera is free again, unless a newer exposure has begun.</summary>
    private void IdleIfCurrent(int generation)
    {
        if (Volatile.Read(ref _exposureGeneration) == generation)
        {
            Interlocked.Exchange(ref _cameraState, (int)CameraState.Idle);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Puts back the drive (and the lockup setting) a lockup capture changed, once the body takes writes again: it refuses
    /// them while it holds a frame, so FC.SDK leaves the restore pending until the frame is released. <paramref name="persist"/>
    /// asks again for a few seconds, before a release, which must not run on the self-timer drive; the object queue asks once.
    /// </summary>
    private async ValueTask ApplyPendingMirrorLockupRestoreAsync(CanonCamera camera, bool persist, CancellationToken ct)
    {
        var attempts = persist ? CanonBusyRetry.Attempts : 1;
        for (var tries = 0; tries < attempts && camera.PendingMirrorLockupRestore is not null; tries++)
        {
            if (tries > 0)
            {
                await TimeProvider.SleepAsync(CanonBusyRetry.Delay, ct);
            }

            await camera.ApplyPendingMirrorLockupRestoreAsync(ct);
        }

        if (persist && camera.PendingMirrorLockupRestore is { } left)
        {
            Logger.LogWarning("{Name} would not take back {Pending} after a mirror lockup exposure", Name, left);
        }
    }

    /// <summary>
    /// Applies a Canon setter, asking again while the body answers busy unless <paramref name="askAgainWhileBusy"/> is false,
    /// logging Info on OK and Debug on reject.
    /// </summary>
    private async ValueTask TrySetAsync(Func<Task<EdsError>> setter, string name, CancellationToken ct, bool askAgainWhileBusy = true)
    {
        try
        {
            var result = askAgainWhileBusy ? await CanonBusyRetry.RunAsync(setter, TimeProvider, ct) : await setter();
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

    /// <summary>
    /// The duration Tv code <paramref name="code"/> stands for: Canon's code is the APEX time value in eighths of a stop above
    /// 0x38 (1 s), so each 8 halves the time. It is the exact power of two (0x1D is 10.4 s, 0x10 32 s), where the body's own
    /// label is rounded (10", 30"): the frame's EXIF is what an exposure records, this is what picks one.
    /// </summary>
    internal static TimeSpan TvDuration(uint code)
    {
        return TimeSpan.FromSeconds(Math.Pow(2.0, (0x38 - (int)code) / 8.0));
    }

    /// <summary>The Tv codes a timed exposure can use: 30 s (0x10) to 1/8000 (0xA0); below is bulb (0x0C) and auto (0).</summary>
    private static bool IsTimedTv(uint code)
    {
        return code is >= 0x10 and <= 0xA0;
    }

    /// <summary>
    /// The Tv code among those the body announces as <paramref name="allowed"/> whose duration is closest to
    /// <paramref name="duration"/> in stops. A body that announces none is given a third-stop code (the whole stops and the
    /// two between each, 3 and 5 eighths on), as every EOS offers by default.
    /// </summary>
    internal static uint ClosestTv(TimeSpan duration, IReadOnlyCollection<uint>? allowed)
    {
        var offered = allowed?.Where(IsTimedTv).ToArray() ?? [];
        var candidates = offered.Length > 0
            ? offered
            : Enumerable.Range(0x10, 0xA0 - 0x10 + 1).Select(c => (uint)c).Where(c => (c & 7) is 0 or 3 or 5).ToArray();

        var wanted = Math.Log2(Math.Max(duration.TotalSeconds, 1e-4));
        var best = candidates[0];
        var bestStops = double.MaxValue;
        foreach (var code in candidates)
        {
            var stops = Math.Abs(Math.Log2(TvDuration(code).TotalSeconds) - wanted);
            if (stops < bestStops)
            {
                bestStops = stops;
                best = code;
            }
        }

        return best;
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

    // A still sub's active-area plane, handed back by the frame's release (see WaitAndDownloadAsync).
    private readonly Imaging.PlaneRecycler _stillPlanes = new Imaging.PlaneRecycler(nameof(CanonCameraDriver) + ".Still");

    // The Live View frames' planes, handed back by each frame's release (see CaptureVideoAsync).
    private readonly Imaging.PlaneRecycler _evfPlanes = new Imaging.PlaneRecycler(nameof(CanonCameraDriver) + ".LiveView");

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

                // Into recycled planes: the loop consuming this stream releases each frame before it asks
                // for the next (PlanetaryCaptureController), and fresh planes plus the RGBA intermediate were
                // 21.6 MB per 1024 x 680 frame at the EVF's 15 to 30 fps.
                if (!Image.TryDecodeRaster(jpeg, _evfPlanes, out var frame))
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
        await DisconnectAsync();
        if (_decodeTask is not null)
        {
            try { await _decodeTask; } catch { /* swallow */ }
        }
    }
}
