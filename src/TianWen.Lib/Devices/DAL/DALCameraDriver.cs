using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.DAL;

internal abstract class DALCameraDriver<TDevice, TDeviceInfo> : DALDeviceDriverBase<TDevice, TDeviceInfo>, ICameraDriver
    where TDevice : DeviceBase
    where TDeviceInfo : struct, ICMOSNativeInterface
{
    protected record class NativeBuffer(nint Pointer, int Size);

    const int IMAGE_STATE_NO_IMG = 0;
    const int IMAGE_STATE_READY_TO_DOWNLOAD = 1;
    const int IMAGE_STATE_DOWNLOADED = 2;

    private CameraSettings _cameraSettings;
    private CameraSettings _exposureSettings;
    private ExposureData? _exposureData;

    /// <summary>
    /// Frames started since this camera was opened; -1 until the first one. Reset in
    /// <see cref="InitCamera"/>, which runs on every connect, so it counts per OPEN and not per
    /// process: re-opening the same body starts again at 0, which is the whole point of it.
    /// </summary>
    /// <remarks>
    /// <para>Incremented when an exposure STARTS rather than when it is read out, so the ordinal is
    /// fixed for the whole exposure and is already answerable while the frame is in flight. A frame
    /// that starts and then fails to read out therefore consumes its number, which is the honest
    /// record of what the camera was asked to do.</para>
    /// <para>Counting here and not in each concrete driver is what makes it true for ZWO and QHY
    /// alike; see <see cref="ICameraDriver.FrameSequence"/> for why it exists at all.</para>
    /// </remarks>
    private long _frameSequence = -1;

    /// <summary>
    /// What the SENSOR says about its own geometry, in unbinned photosites from the readout origin,
    /// read once per connect. Null when the body declares none, which is the common case.
    /// </summary>
    private Geometry.PixelRect? _sensorEffectiveArea;

    /// <summary>The shielded strip in the same coordinates, null when the body exposes none.</summary>
    private Geometry.PixelRect? _sensorOverscanArea;
    private IReadOnlySet<BitDepth> _supportedBitDepth = ImmutableHashSet.Create<BitDepth>();

    /// <summary>
    /// Camera state
    /// </summary>
    private volatile CameraState _camState = CameraState.Idle;
    private int _pulseGuideDirections;

    /// <summary>
    /// Holds a native (COM) buffer that can be filled by the native ASI SDK.
    /// </summary>
    private NativeBuffer? _nativeBuffer;

    // Initialise variables to hold values required for functionality tested by Conform

    private int _camImageReady = 0;
    private Imaging.Channel? _camImageArray;
    private readonly System.Collections.Concurrent.ConcurrentBag<float[,]> _freeBuffers = [];
    private readonly ITimer?[] _pulseGuiderTimers = new ITimer?[4];

    public DALCameraDriver(TDevice device, IServiceProvider serviceProvider) : base(device, serviceProvider)
    {
        DeviceConnectedEvent += DALCameraDriver_DeviceConnectedEvent;
    }

    private Imaging.AdcResolution? AdcDepth { get; set; }

    public bool CanGetCoolerPower { get; private set; }

    public bool CanGetCoolerOn { get; private set; }

    public bool CanSetCoolerOn { get; private set; }

    public bool CanGetHeatsinkTemperature { get; private set; }

    public bool CanGetCCDTemperature { get; private set; }

    public bool CanSetCCDTemperature { get; private set; }

    public bool CanStopExposure { get; } = true;

    public bool CanAbortExposure { get; } = true;

    public bool CanFastReadout { get; private set; }

    public bool CanSetBitDepth => _supportedBitDepth.Count > 1;

    public bool CanPulseGuide { get; private set; }

    /// <summary>
    /// Matches <see cref="CanPulseGuide"/>: an ST-4 port is four independent relay lines, and this
    /// driver already keys its stop timers per DIRECTION (<c>_pulseGuiderTimers[(int)direction]</c>)
    /// with the in-flight set held as a CAS'd bitmask, so a diagonal pulse needs nothing new.
    /// </summary>
    public bool CanPulseGuideSimultaneously => CanPulseGuide;

    public bool UsesGainValue { get; private set; }

    public bool UsesGainMode => false;

    public bool UsesOffsetValue { get; private set; }

    public bool UsesOffsetMode => true;

    public double PixelSizeX { get; private set; } = double.NaN;

    public double PixelSizeY { get; private set; } = double.NaN;

    public short MaxBinX { get; private set; }

    public short MaxBinY { get; private set; }

    public int CameraXSize { get; private set; } = int.MinValue;

    public int CameraYSize { get; private set; } = int.MinValue;

    public ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected ? (BitDepth?)_cameraSettings.BitDepth : null);

    public ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default)
    {
        if (Connected && value is { } bitDepth && _supportedBitDepth.Contains(bitDepth))
        {
            _cameraSettings = _cameraSettings with { BitDepth = bitDepth };
        }
        return ValueTask.CompletedTask;
    }

    public abstract double ExposureResolution { get; }

    protected abstract Exception NotConnectedException();

    protected abstract Exception OperationalException(CMOSErrorCode errorCode, string message);

    public int BinX
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            return _cameraSettings.BinX;
        }

        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                _cameraSettings = RebinnedToFullFrame(_cameraSettings with { BinX = (byte)value }, value);
            }
        }
    }

    /// <summary>
    /// Rescales the region of interest to the full frame AT THE NEW BIN.
    /// </summary>
    /// <remarks>
    /// <para><b>Width and Height are in BINNED pixels, and changing the bin without rescaling them
    /// asks the sensor for a region it does not have.</b> The ROI is set at exposure time as
    /// <c>SetROIFormat(Width, Height, BinX, ...)</c>, so a body connected at 3856 x 2180 and then
    /// switched to bin 2 asked for 3856 x 2180 BINNED pixels, which is four times the binned sensor.
    /// Measured on a Uranus-C: the frame came back at the full 3856 x 2180 with only the valid
    /// quarter populated and the rest zero, and nothing failed. A bias taken that way read a mean of
    /// 8.2 ADU against the correct 32.5, purely because three quarters of it was zero.</para>
    /// <para>Resetting to the full binned frame is the least surprising behaviour and matches what a
    /// caller changing only the bin means. A caller that wants a sub-frame sets
    /// <see cref="NumX"/>/<see cref="NumY"/> AFTER the bin, which is the ASCOM ordering anyway,
    /// since those are expressed in binned pixels and their meaning changes with the bin.</para>
    /// <para>The start position is reset with them: an offset valid at bin 1 can sit outside the
    /// binned sensor entirely.</para>
    /// </remarks>
    private CameraSettings RebinnedToFullFrame(CameraSettings settings, int bin)
        => settings with
        {
            Width = _deviceInfo.MaxWidth / bin,
            Height = _deviceInfo.MaxHeight / bin,
            StartX = 0,
            StartY = 0
        };

    public int BinY
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            return _cameraSettings.BinY;
        }

        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                _cameraSettings = RebinnedToFullFrame(_cameraSettings with { BinY = (byte)value }, value);
            }
        }
    }

    public int StartX
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            return _cameraSettings.StartX;
        }

        set
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            else if (value >= 0 && value * BinX < CameraXSize)
            {
                _cameraSettings = _cameraSettings with { StartX = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), "StartX must be between 0 and Camera size (binned)");
            }
        }
    }

    public int StartY
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }

            return _cameraSettings.StartY;
        }

        set
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            else if (value >= 0 && value * BinY < CameraYSize)
            {
                _cameraSettings = _cameraSettings with { StartY = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), "StartY must be between 0 and Camera size (binned)");
            }
        }
    }

    public int NumX
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }

            // Width, not Height. Both accessors returned Height, so NumX reported the frame's height
            // on every non-square sensor and a caller reading it back got a square ROI.
            return _cameraSettings.Width;
        }

        set
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            // <=, not <. The bound is inclusive: the whole point of a binned full frame is that
            // value * BinX EQUALS the sensor width, so the strict test rejected precisely the most
            // common ROI there is (1928 x 2 == 3856 on an IMX585 at bin 2) and left no way to ask
            // for the full binned frame at all.
            else if (value >= 1 && value * BinX <= CameraXSize)
            {
                _cameraSettings = _cameraSettings with { Width = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Width must be between 1 and Camera size (binned)");
            }
        }
    }

    public int NumY
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            // <=, not <; see NumX for why the strict bound made a full binned frame unreachable.
            else if (value >= 1 && value * BinY <= CameraYSize)
            {
                _cameraSettings = _cameraSettings with { Height = value };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Height must be between 1 and Camera size (binned)");
            }
        }
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public short GainMin { get; private set; } = short.MinValue;

    public short GainMax { get; private set; } = short.MinValue;

    public IReadOnlyList<string> Offsets => throw new InvalidOperationException($"{nameof(Offsets)} is not supported");

    public DateTimeOffset? LastExposureStartTime => _exposureData?.StartTime;

    public TimeSpan? LastExposureDuration => _exposureData?.ActualDuration;

    public FrameType LastExposureFrameType => _exposureData?.FrameType ?? FrameType.None;

    public virtual string? SensorModelName => _deviceInfo.SensorModel;

    public SensorType SensorType { get; private set; }

    public int BayerOffsetX { get; private set; } = int.MinValue;

    public int BayerOffsetY { get; private set; } = int.MinValue;

    /// <summary>
    /// TODO: implement trigger
    /// </summary>
    public ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected && CanFastReadout && _cameraSettings.FastReadout);

    public ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (Connected && CanFastReadout)
        {
            _cameraSettings = _cameraSettings with { FastReadout = value };
        }
        return ValueTask.CompletedTask;
    }

    public int MaxADU
    {
        get
        {
            if (Connected && _cameraSettings.BitDepth.IsIntegral && _cameraSettings.BitDepth.BitSize is { } bitSize and > 0)
            {
                return bitSize switch
                {
                    8 => byte.MaxValue,
                    // Native ADC full-scale (16383 for the 14-bit ASI533MC Pro): the vendor SDK hands
                    // TianWen native-scale values -- TianWen does NOT left-shift them into the 16-bit
                    // container on capture. (N.I.N.A.'s FITS files from the same cameras span [0, 65532]
                    // only because N.I.N.A. multiplies on recording; do not infer the SDK's delivered
                    // scale from N.I.N.A. files -- for those, the container full-scale
                    // BitDepthEx.UnsignedFullScale is the right divisor, see the dataset builder.)
                    // Bits <= 16 also guards the (int) cast against a nonsense >16-bit ADC report.
                    16 => AdcDepth is { Bits: <= 16 } adcDepth ? (int)adcDepth.FullScaleAdu : ushort.MaxValue,
                    _ => int.MinValue
                };
            }
            return int.MinValue;
        }
    }

    public double ElectronsPerADU { get; private set; } = double.NaN;

    public double FullWellCapacity => ElectronsPerADU * MaxADU;

    public Imaging.Channel? ImageData
    {
        get
        {
            switch (Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_DOWNLOADED, IMAGE_STATE_READY_TO_DOWNLOAD))
            {
                case IMAGE_STATE_NO_IMG:
                    throw new InvalidOperationException("Call to ImageArray before the first image has been taken!");

                case IMAGE_STATE_READY_TO_DOWNLOAD:
                    _camState = CameraState.Download;

                    return DownloadImage(_exposureSettings);
            }

            return _camImageArray;
        }
    }

    public void ReleaseImageData()
    {
        _camImageArray = null;
    }

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
    {
        if (Connected
            && _deviceInfo.GetControlValue(CMOSControlType.Gain, out var gain, out _) is CMOSErrorCode.Success
            && gain >= GainMin
            && gain <= GainMax
        )
        {
            return ValueTask.FromResult((short)gain);
        }
        return ValueTask.FromResult(short.MinValue);
    }

    public ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
    {
        if (value < GainMin || value > GainMax || _deviceInfo.SetControlValue(CMOSControlType.Gain, value) is not CMOSErrorCode.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Gain must be between {GainMin} and {GainMax} inclusive");
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<string> Gains => throw new InvalidOperationException("Gains is not supported");

    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default)
    {
        if (Connected
            && _deviceInfo.GetControlValue(CMOSControlType.Brightness, out var offset, out _) is CMOSErrorCode.Success
            && offset >= OffsetMin
            && offset <= OffsetMax
        )
        {
            return ValueTask.FromResult(offset);
        }
        return ValueTask.FromResult(int.MinValue);
    }

    public ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default)
    {
        if (value < OffsetMin || value > OffsetMax || _deviceInfo.SetControlValue(CMOSControlType.Brightness, value) is not CMOSErrorCode.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Offset must be between {OffsetMin} and {OffsetMax} inclusive");
        }
        return ValueTask.CompletedTask;
    }

    private void SetImageReadyToDownload(TimeSpan? actualDuration)
    {
        if (Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_READY_TO_DOWNLOAD, IMAGE_STATE_NO_IMG) is IMAGE_STATE_NO_IMG
                                    && _exposureData is { } data
                                    && !data.ActualDuration.HasValue
                                )
        {
            _exposureData = data with { ActualDuration = actualDuration ?? data.IntendedDuration };
        }
    }


    private void DALCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            InitCamera();
        }
    }

    private void InitCamera()
    {
        // set max binning values
        {
            short maxBin = 0;
            foreach (var supportedBin in _deviceInfo.SupportedBins)
            {
                if (supportedBin is 0)
                {
                    break;
                }
                else if (supportedBin is <= short.MaxValue)
                {
                    maxBin = Math.Max(maxBin, (short)supportedBin);
                }
            }
            MaxBinX = maxBin;
            MaxBinY = maxBin;
        }

        // Bayer pattern
        if (_deviceInfo.BayerPattern is BayerPattern.Monochrome)
        {
            BayerOffsetX = 0;
            BayerOffsetY = 0;
            SensorType = SensorType.Monochrome;
        }
        else
        {
            (BayerOffsetX, BayerOffsetY) = _deviceInfo.BayerPattern.GetOffsets();
            SensorType = SensorType.RGGB;
        }

        // update supported bidepth set
        {
            var supported = new HashSet<BitDepth>();

            foreach (var pixelFormat in _deviceInfo.SupportedPixelDataFormats)
            {
                if (pixelFormat.ToBitDepth() is { } bitDepth)
                {
                    supported.Add(bitDepth);
                }
            }

            _ = Interlocked.Exchange(ref _supportedBitDepth, supported);
        }

        var isCoolerCam = _deviceInfo.HasCooler;
        CanSetCCDTemperature = isCoolerCam;
        CanGetCoolerPower = isCoolerCam;
        CanGetCoolerOn = isCoolerCam;
        CanSetCoolerOn = isCoolerCam;
        CanPulseGuide =  _deviceInfo.HasST4Port;

        // HeatSinkTemperature is not available for DAL cameras
        CanGetHeatsinkTemperature = false;

        try
        {
            CanGetCCDTemperature = _deviceInfo.GetControlValue(CMOSControlType.TemperatureDeci, out _, out _) is CMOSErrorCode.Success;
        }
        catch
        {
            CanGetCCDTemperature = false;
        }

        var highestPossibleBitDepth = _supportedBitDepth.Where(x => x.IsIntegral).OrderByDescending(x => x.BitSize).First();
        _cameraSettings = new CameraSettings(0, 0, CameraXSize  = _deviceInfo.MaxWidth, CameraYSize = _deviceInfo.MaxHeight, 1, 1, highestPossibleBitDepth, false);
        PixelSizeX = PixelSizeY = _deviceInfo.PixelSize;
        ElectronsPerADU = _deviceInfo.ElectronPerADU is var elecPerADU and > 0f ? elecPerADU : double.NaN;
        AdcDepth = _deviceInfo.BitDepth is > 0 and <= 32 ? new Imaging.AdcResolution(_deviceInfo.BitDepth) : null;

        CanFastReadout = _deviceInfo.TryGetControlRange(CMOSControlType.HighSpeedMode, out _, out _);

        // min max offset and gain
        if (_deviceInfo.TryGetControlRange(CMOSControlType.Brightness, out var offsetMin, out var offsetMax))
        {
            OffsetMin = offsetMin;
            OffsetMax = offsetMax;
        }
        else
        {
            OffsetMin = int.MinValue;
            OffsetMax = int.MinValue;
        }

        if (_deviceInfo.TryGetControlRange(CMOSControlType.Gain, out var gainMin, out var gainMax))
        {
            GainMin = gainMin <= short.MaxValue ? (short)gainMin : short.MinValue;
            GainMax = gainMax <= short.MaxValue ? (short)gainMax : short.MaxValue;
        }
        else
        {
            GainMin = short.MinValue;
            GainMax = short.MinValue;
        }

        // A fresh open is a fresh sequence: this runs on every connect, and the first frames after an
        // open are exactly what the number exists to identify.
        Interlocked.Exchange(ref _frameSequence, -1);

        // Sensor geometry is a property of the BODY, so it is asked once per connect rather than per
        // frame. Both are routinely absent (a QHY178M reports its effective area as the whole readout
        // and an overscan of 0 x 0), and absent is a real answer, not a failure.
        _sensorEffectiveArea = _deviceInfo.TryGetEffectiveArea(out var effX, out var effY, out var effW, out var effH)
            ? new Geometry.PixelRect(effX, effY, effW, effH)
            : null;
        _sensorOverscanArea = _deviceInfo.TryGetOverscanArea(out var osX, out var osY, out var osW, out var osH)
            ? new Geometry.PixelRect(osX, osY, osW, osH)
            : null;

        var initControlValues = new Dictionary<CMOSControlType, int>
        {
            [CMOSControlType.Flip] = 0,
            [CMOSControlType.Gamma] = 50,
            [CMOSControlType.HighSpeedMode] = Convert.ToInt32(_cameraSettings.FastReadout),
            [CMOSControlType.MonoBin] = 0,
            [CMOSControlType.HardwareBin] = 0,
            [CMOSControlType.PatternAdjust] = 0,
            [CMOSControlType.BandwidthOverload] = 50,
            [CMOSControlType.EnableDDR] = 1,
            [CMOSControlType.Gain] = (int)MathF.FusedMultiplyAdd(GainMax - GainMin, 0.4f, GainMin),
            [CMOSControlType.Brightness] = (int)MathF.FusedMultiplyAdd(OffsetMax - OffsetMin, 0.1f, OffsetMin),
        };

        foreach (var pair in initControlValues)
        {
            // ignore
            _ =_deviceInfo.SetControlValue(pair.Key, pair.Value);
        }

        SetNeutralWhiteBalance();
    }

    /// <summary>
    /// Puts the white balance where it applies NO gain, asking the camera what that value is.
    /// </summary>
    /// <remarks>
    /// <para><b>This used to be a literal 50 written to red and blue, which is neutral on exactly
    /// one vendor.</b> ZWO's channels run about [1, 99] with unity at the midpoint, so 50 was right
    /// there and was quietly wrong everywhere else: a Player One body scales [-1200, 1200] about a
    /// neutral of 0, so 50 is a small red and blue lift applied to every frame captured through this
    /// driver. It went unnoticed because NO FITS header records a white balance. It is baked into
    /// the pixels, so the only way to see it afterwards is to measure the per-photosite quantisation
    /// step.</para>
    /// <para>It is not a preview matter. A white balance is a digital gain on the RAW stream, so it
    /// scales the PEDESTAL, and a dark library shot at one balance does not describe lights shot at
    /// another. That is a calibration error, not a colour cast.</para>
    /// <para><b>Green is written only where the body has a green channel.</b> ZWO balances red and
    /// blue against an implicit green and has no <see cref="CMOSControlType.WB_G"/> at all, while
    /// Player One has all three, and writing two of three leaves the third wherever it was last
    /// put.</para>
    /// <para>The 50 remains as the fallback for a device that does not yet answer
    /// <see cref="ICMOSNativeInterface.TryGetWhiteBalanceRange"/>, so this is not a behaviour change
    /// for ZWO or QHY: they keep exactly what they had until each implements the member, and only a
    /// camera that states its own scale is driven by it.</para>
    /// </remarks>
    private void SetNeutralWhiteBalance()
    {
        var hasScale = _deviceInfo.TryGetWhiteBalanceRange(out _, out _, out var neutral);
        if (!hasScale)
        {
            neutral = 50;
        }

        _ = _deviceInfo.SetControlValue(CMOSControlType.WB_R, neutral);
        _ = _deviceInfo.SetControlValue(CMOSControlType.WB_B, neutral);

        if (hasScale && _deviceInfo.HasThreeChannelWhiteBalance)
        {
            _ = _deviceInfo.SetControlValue(CMOSControlType.WB_G, neutral);
        }
    }

    private CameraState GetCameraStateInternal()
    {
        if (_camState is CameraState.Exposing && _deviceInfo.GetExposureStatus(out var snapStatus) is CMOSErrorCode.Success)
        {
            switch (snapStatus)
            {
                case ExposureStatus.Idle:
                case ExposureStatus.Failed:
                    _camState = CameraState.Idle;
                    Interlocked.Exchange(ref _camImageReady, IMAGE_STATE_NO_IMG);
                    break;

                case ExposureStatus.Success:
                    _camState = CameraState.Idle;
                    // do not provide the actual time as it is not clear how long ago it finished
                    SetImageReadyToDownload(null);
                    break;

                case ExposureStatus.Working:
                    _camState = CameraState.Exposing;
                    break;
            }
        }

        return _camState;
    }

    // Async-primary members
    public ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw NotConnectedException();
        }
        else if (GetCameraStateInternal() is CameraState.Error)
        {
            return ValueTask.FromResult(false);
        }

        var isReady = IMAGE_STATE_NO_IMG != Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_NO_IMG, IMAGE_STATE_NO_IMG);

        return ValueTask.FromResult(isReady);
    }

    public ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetCameraStateInternal());

    public ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_deviceInfo.GetControlValue(CMOSControlType.TemperatureDeci, out var intTemp, out _) is CMOSErrorCode.Success ? intTemp * 0.1d : double.NaN);

    public ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(double.NaN);

    public ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw NotConnectedException();
        }
        else if (!CanGetCoolerPower)
        {
            throw OperationalException(CMOSErrorCode.GeneralError, "Getting cooler power on is not supported");
        }
        else if (_deviceInfo.GetControlValue(CMOSControlType.CoolerPowerPercent, out var percentage, out _) is var code and not CMOSErrorCode.Success)
        {
            throw OperationalException(code, $"Failed to get cooler power, with error code {code}");
        }
        else
        {
            return ValueTask.FromResult((double)percentage);
        }
    }

    public ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Connected
            && CanGetCoolerOn
            && _deviceInfo.GetControlValue(CMOSControlType.CoolerOn, out var isOn, out _) is CMOSErrorCode.Success
            && isOn == Convert.ToInt32(true));

    public ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw NotConnectedException();
        }
        else if (!CanSetCoolerOn)
        {
            throw OperationalException(CMOSErrorCode.GeneralError, "Cooler on is not supported");
        }
        else if (_deviceInfo.SetControlValue(CMOSControlType.CoolerOn, Convert.ToInt32(value)) is var code and not CMOSErrorCode.Success)
        {
            throw OperationalException(code, $"Failed to turn cooler {(value ? "on" : "off")}, with error code {code}");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw NotConnectedException();
        }
        else if (!CanSetCCDTemperature)
        {
            throw OperationalException(CMOSErrorCode.GeneralError, "Cooler set CCD temp is not supported");
        }
        else if (_deviceInfo.GetControlValue(CMOSControlType.TargetTemperature, out var val, out _) is not CMOSErrorCode.Success)
        {
            // NOT an error: a camera with no setpoint engaged has no target to report, which QHY
            // signals by answering out of band. Throwing here would be counted as a driver fault by
            // the resilience layer on EVERY telemetry poll and eventually trigger a spurious
            // reconnect, so the honest answer is "unknown", which is what NaN means for this value
            // everywhere it lands (ImageMeta.SetCCDTemperature, the cooling graph).
            return ValueTask.FromResult(double.NaN);
        }
        else
        {
            return ValueTask.FromResult((double)val);
        }
    }

    public ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        // TODO exception
        if (Connected
            && CanSetCCDTemperature
            && _deviceInfo.TryGetControlRange(CMOSControlType.TargetTemperature, out var min, out var max)
            && value >= min
            && value <= max
        )
        {
            _deviceInfo.SetControlValue(CMOSControlType.TargetTemperature, (int)value);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Interlocked.CompareExchange(ref _pulseGuideDirections, 0, 0) is not 0);


    Imaging.Channel DownloadImage(in CameraSettings exposureSettings)
    {
        var w = exposureSettings.Width;
        var h = exposureSettings.Height;
        var nativeBuffer = Interlocked.CompareExchange(ref _nativeBuffer, null, null);

        if (nativeBuffer is null || nativeBuffer.Pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("No native image array present!");
        }

        var expBufferSize = CalculateBufferSize(exposureSettings);
        if (nativeBuffer.Size < expBufferSize)
        {
            throw new InvalidOperationException($"Native buffer size {nativeBuffer.Size} smaller than required {expBufferSize}");
        }

        var dataAfterExpErrorCode = _deviceInfo.GetDataAfterExposure(nativeBuffer.Pointer, expBufferSize);
        if (dataAfterExpErrorCode is not CMOSErrorCode.Success)
        {
            throw new InvalidOperationException($"Getting data after exposure returned {dataAfterExpErrorCode} w={w} h={h} bit={exposureSettings.BitDepth}");
        }

        float[,] channel;
        if (_freeBuffers.TryTake(out var recycled) && recycled.GetLength(0) == h && recycled.GetLength(1) == w)
        {
            channel = recycled;
        }
        else
        {
            channel = new float[h, w];
        }
        float maxValue = 0f, minValue = float.MaxValue;
        switch (exposureSettings.BitDepth.BitSize)
        {
            case 8:
                var bytes = new byte[w * h];
                Marshal.Copy(nativeBuffer.Pointer, bytes, 0, bytes.Length);
                for (var i = 0; i < h; i++)
                {
                    for (var j = 0; j < w; j++)
                    {
                        var @byte = channel[i, j] = bytes[(w * i) + j];
                        maxValue = MathF.Max(@byte, maxValue);
                        minValue = MathF.Min(@byte, minValue);
                    }
                }
                break;

            case 16:
                var shorts = new short[w * h];
                Marshal.Copy(nativeBuffer.Pointer, shorts, 0, shorts.Length);
                for (var i = 0; i < h; i++)
                {
                    for (var j = 0; j < w; j++)
                    {
                        var @short = channel[i, j] = shorts[(w * i) + j];
                        maxValue = MathF.Max(@short, maxValue);
                        minValue = MathF.Min(@short, minValue);
                    }
                }
                break;

            default:
                throw new InvalidOperationException($"Cannot handle bit depth {exposureSettings.BitDepth}");
        }

        // Wrap in ChannelBuffer for ref-counted lifecycle; onRelease recycles the float[,];
        // the buffer travels ON the Channel into GetImageAsync's Image (which harvests the ref).
        var buffer = new Imaging.ChannelBuffer(channel, onRelease: recycledBuf => _freeBuffers.Add(recycledBuf));
        var result = new Imaging.Channel(channel, default, minValue, maxValue, 0) { Buffer = buffer };
        _camImageArray = result;
        _camState = CameraState.Idle;

        return result;
    }

    private void StopExposureInternal()
    {
        if (_camState == CameraState.Idle)
        {
            return;
        }

        if (_deviceInfo.StopExposure() is CMOSErrorCode.Success)
        {
            Interlocked.Exchange(ref _camState, CameraState.Idle);
            SetImageReadyToDownload(_exposureData is { } data ? TimeProvider.GetUtcNow() - data.StartTime : null);
        }
    }

    public ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
    {
        StopExposureInternal();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
    {
        StopExposureInternal();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public long FrameSequence => Interlocked.Read(ref _frameSequence);

    /// <inheritdoc/>
    /// <remarks>
    /// Always <see cref="FrameCounterSource.Software"/>, and deliberately not conditional on the
    /// camera's own capability. QHY does expose a hardware counter, but only as part of BURST mode
    /// (<c>EnableQHYCCDBurstCountFun</c> plus <c>ResetQHYCCDFrameCounter</c>, a sub-mode of continuous
    /// mode) and with no function anywhere in the SDK to read the value back, so there is nothing to
    /// prefer here yet; ZWO offers none at all. Reporting Software is what keeps a consumer from
    /// reading a dense sequence as proof that no frame was dropped.
    /// </remarks>
    public FrameCounterSource FrameCounterSource => FrameCounterSource.Software;

    /// <inheritdoc/>
    public Geometry.PixelRect? DataSection => PictureSection();

    /// <inheritdoc/>
    public Geometry.PixelRect? BiasSection => SectionForStoredFrame(_sensorOverscanArea);

    private Geometry.PixelRect? PictureSection()
    {
        if (SectionForStoredFrame(_sensorEffectiveArea) is not { } area)
        {
            return null;
        }

        // An effective area equal to the whole readout states NOTHING, and must be reported as
        // absent rather than as a full-frame section: the card's only job is to tell a frame with a
        // shielded margin from one without, so stamping the whole raster on every file from a body
        // that has no margin (a QHY178M reports exactly this, 3056 x 2048 with an empty overscan)
        // would destroy the distinction it exists to make.
        return area.X is 0 && area.Y is 0 && area.Width == _deviceInfo.MaxWidth && area.Height == _deviceInfo.MaxHeight
            ? null
            : area;
    }

    /// <summary>
    /// Maps a sensor-stated area onto the frame actually being stored, or null when that mapping is
    /// not certain.
    /// </summary>
    /// <remarks>
    /// The sensor states its geometry in UNBINNED photosites from the readout's own origin, and only
    /// a full-frame unbinned capture carries those coordinates onto the stored raster unchanged.
    /// Under a bin or an ROI the mapping is a translate and a divide whose exact convention varies by
    /// vendor and by which SDK call set the ROI, and <b>a section that is wrong is worse than one
    /// that is absent</b>: absent means "the whole raster is the picture", which is what every
    /// consumer already assumed, while a wrong one silently mislabels which pixels are lit and which
    /// are the black reference. So this declares a section only where it is certain. Widening it
    /// wants a body that actually exposes an overscan to measure against, which the QHY178M does not.
    /// </remarks>
    private Geometry.PixelRect? SectionForStoredFrame(Geometry.PixelRect? sensorArea)
    {
        var settings = _exposureSettings;
        return sensorArea is { } area
            && BinX is 1 && BinY is 1
            && settings.StartX is 0 && settings.StartY is 0
            && settings.Width == _deviceInfo.MaxWidth && settings.Height == _deviceInfo.MaxHeight
            ? area
            : null;
    }

    public ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        var settingsSnapshot = _cameraSettings;

        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), duration, "0.0 upwards");

        int durationInNanoSecs;
        if (_deviceInfo.TryGetControlRange(CMOSControlType.Exposure, out var min, out var max))
        {
            durationInNanoSecs = Math.Min(max, Math.Max(min, (int)Math.Round(duration.TotalMilliseconds * 1000)));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Could not find min,max");
        }

        var getROIErrorCode = _deviceInfo.GetROIFormat(out var currentWidth, out var currentHeight, out var currentBin, out var currentImgType);
        var getStartXYErrorCode = _deviceInfo.GetStartPosition(out var currentStartX, out var currentStartY);

        // check if any parameters that require stopping expore changed
        bool bitDepthChanged;
        if (getROIErrorCode is CMOSErrorCode.Success && getStartXYErrorCode is CMOSErrorCode.Success)
        {
            bitDepthChanged = currentImgType.ToBitDepth() != settingsSnapshot.BitDepth;

            if (bitDepthChanged
                || currentBin != BinX
                || currentBin != BinY
                || currentWidth != settingsSnapshot.Width
                || currentHeight != settingsSnapshot.Height
                || currentStartX != settingsSnapshot.StartX
                || currentStartY != settingsSnapshot.StartY
            )
            {
                StopExposureInternal();

                var setROIErrorCode = _deviceInfo.SetROIFormat(settingsSnapshot.Width, settingsSnapshot.Height, BinX, settingsSnapshot.BitDepth.ToRawPixelFormat());
                var setStartXYErrorCode = _deviceInfo.SetStartPosition(settingsSnapshot.StartX, settingsSnapshot.StartY);
                if (setROIErrorCode is not CMOSErrorCode.Success)
                {
                    _camState = CameraState.Error;
                    throw OperationalException(setROIErrorCode, $"Failed to set ROI format: {settingsSnapshot}");
                }
                else if (setStartXYErrorCode is not CMOSErrorCode.Success)
                {
                    _camState = CameraState.Error;
                    throw OperationalException(setStartXYErrorCode, $"Failed to set X-Y offset of ROI to x={settingsSnapshot.StartX}, y={settingsSnapshot.StartY}");
                }
            }
        }
        else if (getROIErrorCode is not CMOSErrorCode.Success)
        {
            _camState = CameraState.Error;

            throw OperationalException(getROIErrorCode, "Failed to retrieve current ROI format");
        }
        else
        {
            _camState = CameraState.Error;

            throw OperationalException(getStartXYErrorCode, "Failed to retrieve X-Y offset of ROI");
        }

        // reallocate buffer if required
        int bufferSize = CalculateBufferSize(_cameraSettings);
        var existingBuffer = Interlocked.CompareExchange(ref _nativeBuffer, null, null);

        if (bitDepthChanged || existingBuffer is null || existingBuffer.Pointer == IntPtr.Zero || existingBuffer.Size < bufferSize)
        {
            AllocateNativeBuffer(bufferSize);
        }

        // check if we need to update exposure time
        // TODO: Support auto-exposure
        var getExposureErrorCode = _deviceInfo.GetControlValue(CMOSControlType.Exposure, out int currentExposure, out _);
        if (getExposureErrorCode is CMOSErrorCode.Success)
        {
            if (currentExposure != durationInNanoSecs)
            {
                var setExposureErrorCode = _deviceInfo.SetControlValue(CMOSControlType.Exposure, durationInNanoSecs);
                if (setExposureErrorCode is not CMOSErrorCode.Success)
                {
                    _camState = CameraState.Error;
                    throw OperationalException(setExposureErrorCode, $"Failed to set exposure to {durationInNanoSecs} ns");
                }
            }
        }
        else
        {
            _camState = CameraState.Error;
            throw OperationalException(getExposureErrorCode, "Failed to retrieve current exposure settings");
        }

        var startExposureErrorCode = frameType.NeedsOpenShutter
            ? _deviceInfo.StartLightExposure()
            : _deviceInfo.StartDarkExposure();
        if (startExposureErrorCode is CMOSErrorCode.Success)
        {
            _camState = CameraState.Exposing;
            Interlocked.Increment(ref _frameSequence);
            var startTime = TimeProvider.GetUtcNow();
            _deviceInfo.GetControlValue(CMOSControlType.Gain, out var currentGain, out _);
            _deviceInfo.GetControlValue(CMOSControlType.Brightness, out var currentOffset, out _);
            _exposureData = new ExposureData(startTime, duration, null, frameType, currentGain, currentOffset);
            // ensure that on image readout we use the settings that the image was exposed with
            _exposureSettings = settingsSnapshot;
            Interlocked.Exchange(ref _camImageReady, IMAGE_STATE_NO_IMG);

            return ValueTask.FromResult(startTime);
        }
        else
        {
            _camState = CameraState.Error;
            throw OperationalException(startExposureErrorCode, $"Failed to start exposure frame type={frameType} duration={durationInNanoSecs} ns");
        }
    }

    public ValueTask StartPulseGuideAsync(GuideDirection guideDirection, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var timer = TimeProvider.CreateTimer(StopPulseGuiding, guideDirection, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // A device that times its own pulse is TOLD the duration; one that does not is started here
        // and stopped by the timer below. The timer runs either way, because the in-flight bookkeeping
        // that IsPulseGuidingAsync reports has to be cleared when the pulse ends whoever ended it.
        var selfTimed = _deviceInfo.CanPulseGuideForDuration;
        var startCode = selfTimed
            ? _deviceInfo.PulseGuideOn(guideDirection, duration)
            : _deviceInfo.PulseGuideOn(guideDirection);
        if (startCode is var code and not CMOSErrorCode.Success)
        {
            throw OperationalException(code, $"Failed to pulse guide {guideDirection} for {duration:o}");
        }
        else
        {
            UpdateGuideDirections(guideDirection, (existing, bit) => existing | bit);

            Interlocked.Exchange(ref _pulseGuiderTimers[(int)guideDirection], timer)?.Dispose();
            timer.Change(duration, Timeout.InfiniteTimeSpan);
        }
        return ValueTask.CompletedTask;
    }

    private void UpdateGuideDirections(GuideDirection guideDirection, Func<int, int, int> updateFunc)
    {
        var dirAsInt = (int)guideDirection;
        var bit = 1 << dirAsInt;
        var existing = _pulseGuideDirections;
        int set;
        do
        {
            set = updateFunc(existing, bit);
        } while ((existing = Interlocked.CompareExchange(ref _pulseGuideDirections, set, existing)) != existing);
    }

    private void StopPulseGuiding(object? obj)
    {
        if (obj is GuideDirection guideDirection)
        {
            // Only ASK to stop a device that needs stopping. On one that timed its own pulse the
            // pulse is already over, and an extra stop is at best a wasted call and at worst a
            // command the SDK does not have, so the timer here only clears the in-flight state.
            var code = _deviceInfo.CanPulseGuideForDuration
                ? CMOSErrorCode.Success
                : _deviceInfo.PulseGuideOff(guideDirection);
            if (code is not CMOSErrorCode.Success)
            {
                Logger.LogError("Failed to stop guiding in direction {GuideDirection} due to error: {ErrorCode}", guideDirection, code);
            }
            else
            {
                UpdateGuideDirections(guideDirection, (existing, bit) => existing & ~bit);

                Interlocked.Exchange(ref _pulseGuiderTimers[(int)guideDirection], null)?.Dispose();
            }
        }
        else
        {
            Logger.LogCritical("Invalid state: {obj} in stop pulse guiding callback", obj);
        }
    }

    /// <summary>
    /// Allocates memory from the COM task scheduler.
    /// </summary>
    /// <param name="bufferSize">new buffer size in bytes</param>
    /// <returns>True if a buffer is allocated.</returns>
    private void AllocateNativeBuffer(int bufferSize)
    {
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), $"Buffer size {bufferSize} is not large enough");
        }

        var newBuffer = new NativeBuffer(Marshal.AllocCoTaskMem(bufferSize), bufferSize);

        var existingBuffer = Interlocked.Exchange(ref _nativeBuffer, newBuffer);

        if (existingBuffer is not null && existingBuffer.Pointer != IntPtr.Zero && existingBuffer.Pointer != newBuffer.Pointer)
        {
            Marshal.FreeCoTaskMem(existingBuffer.Pointer);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            for (var i = 0; i < _pulseGuiderTimers.Length; i++)
            {
                Interlocked.Exchange(ref _pulseGuiderTimers[i], null)?.Dispose();
            }
        }
    }

    protected override void DisposeUnmanaged()
    {
        var existingBuffer = Interlocked.Exchange(ref _nativeBuffer, null);

        if (existingBuffer is not null && existingBuffer.Pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(existingBuffer.Pointer);
        }
    }

    static int CalculateBufferSize(in CameraSettings settings) => settings.BitDepth.BitSize / 8 * settings.Width * settings.Height;

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int? Aperture { get; set; }

    public int FocusPosition { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? SiteElevation { get; set; }

    public Target? Target { get; set; }

    /// <inheritdoc />
    public Imaging.GuidingStats? GuideStats { get; set; }
    #endregion
}
