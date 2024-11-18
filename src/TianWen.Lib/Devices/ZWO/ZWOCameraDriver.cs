using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Dynamic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using static ZWOptical.SDK.ASICamera2;
using static ZWOptical.SDK.ASICamera2.ASI_BOOL;
using static ZWOptical.SDK.ASICamera2.ASI_ERROR_CODE;

namespace TianWen.Lib.Devices.ZWO;

internal class ZWOCameraDriver : ZWODeviceDriverBase<ASI_CAMERA_INFO>, ICameraDriver
{
    record class NativeBuffer(IntPtr Pointer, int Size);

    const int IMAGE_STATE_NO_IMG = 0;
    const int IMAGE_STATE_READY_TO_DOWNLOAD = 1;
    const int IMAGE_STATE_DOWNLOADED = 2;

    private CameraSettings _cameraSettings;
    private CameraSettings _exposureSettings;
    private ExposureData? _exposureData;
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
    private Float32HxWImageData? _camImageArray;
    private readonly ITimer?[] _pulseGuiderTimers = new ITimer?[4];

    public ZWOCameraDriver(ZWODevice device, IExternal external) : base(device, external)
    {
        DeviceConnectedEvent += ZWOCameraDriver_DeviceConnectedEvent;
    }

    private void ZWOCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            ProcessDeviceInfo(InitCamera);
        }
    }

    private void InitCamera(in ASI_CAMERA_INFO camInfo)
    {
        // set max binning values
        {
            short maxBin = 0;
            foreach (var supportedBin in camInfo.SupportedBins)
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
        if (camInfo.IsCoolerCam is ASI_TRUE)
        {
            BayerOffsetX = camInfo.BayerPattern.BayerXOffset();
            BayerOffsetY = camInfo.BayerPattern.BayerYOffset();
        }

        // update supported bidepth set
        {
            var supported = new HashSet<BitDepth>();

            foreach (var videoFormat in camInfo.SupportedVideoFormat)
            {
                if (videoFormat == ASI_IMG_TYPE.ASI_IMG_END)
                {
                    break;
                }
                else if (videoFormat.ToBitDepth() is { } bitDepth)
                {
                    supported.Add(bitDepth);
                }
            }

            _ = Interlocked.Exchange(ref _supportedBitDepth, supported);
        }

        var isCoolerCam = camInfo.IsCoolerCam is ASI_TRUE;
        CanSetCCDTemperature = isCoolerCam;
        CanGetCoolerPower = isCoolerCam;
        CanGetCoolerOn = isCoolerCam;
        CanSetCoolerOn = isCoolerCam;
        CanPulseGuide = camInfo.ST4Port is ASI_TRUE;

        CanFastReadout = TryGetControlRange(camInfo.CameraID, ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE, out _, out _);

        try
        {
            CanGetHeatsinkTemperature = !double.IsNaN(HeatSinkTemperature);
        }
        catch
        {
            CanGetHeatsinkTemperature = false;
        }

        try
        {
            CanGetCCDTemperature = !double.IsNaN(CCDTemperature);
        }
        catch
        {
            CanGetCCDTemperature = false;
        }

        {
            OffsetMin = TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_BRIGHTNESS, out var min, out _) ? min : int.MinValue;
            OffsetMax = TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_BRIGHTNESS, out _, out var max) ? max : int.MinValue;
        }
        {
            GainMin = TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_GAIN, out var min, out _) && min <= short.MaxValue ? (short)min : short.MinValue;
            GainMax = TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_GAIN, out _, out var max) && max <= short.MaxValue ? (short)max : short.MinValue;
        }

        var highestPossibleBitDepth = _supportedBitDepth.Where(x => x.IsIntegral()).OrderByDescending(x => x.BitSize()).First();
        _cameraSettings = new CameraSettings(0, 0, CameraXSize  = camInfo.MaxWidth, CameraYSize = camInfo.MaxHeight, 1, 1, highestPossibleBitDepth, false);
        PixelSizeX = PixelSizeY = camInfo.PixelSize;
        ElectronsPerADU = camInfo.ElecPerADU is var elecPerADU and > 0f ? elecPerADU : double.NaN;
        ADCBitDepth = camInfo.BitDepth;


        var initControlValues = new Dictionary<ASI_CONTROL_TYPE, int>
        {
            [ASI_CONTROL_TYPE.ASI_FLIP] = 0,
            [ASI_CONTROL_TYPE.ASI_GAMMA] = 50,
            [ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE] = Convert.ToInt32(_cameraSettings.FastReadout),
            [ASI_CONTROL_TYPE.ASI_MONO_BIN] = 0,
            [ASI_CONTROL_TYPE.ASI_HARDWARE_BIN] = 0,
            [ASI_CONTROL_TYPE.ASI_WB_R] = 50,
            [ASI_CONTROL_TYPE.ASI_WB_B] = 50,
            [ASI_CONTROL_TYPE.ASI_PATTERN_ADJUST] = 0,
            [ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD] = 50,
            [ASI_CONTROL_TYPE.ASI_ENABLE_DDR] = 1,
            [ASI_CONTROL_TYPE.ASI_GAIN] = (int)MathF.FusedMultiplyAdd(GainMax - GainMin, 0.4f, GainMin),
            [ASI_CONTROL_TYPE.ASI_BRIGHTNESS] = (int)MathF.FusedMultiplyAdd(OffsetMax - OffsetMin, 0.1f, OffsetMin),
        };

        foreach (var pair in initControlValues)
        {
            // ignore
            _ = ASISetControlValue(camInfo.CameraID, pair.Key, pair.Value);
        }
    }

    private int ADCBitDepth { get; set; } = int.MinValue;

    public override string? Description { get; } = $"ZWO Camera driver using C# SDK wrapper v{ASIGetSDKVersion}";

    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        if (ASIInitCamera(_deviceInfo.ID) is ASI_SUCCESS)
        {
            return ValueTask.FromResult(true);
        }
        else
        {
            // close this device again as we failed to initalize it
            _deviceInfo.Close();

            return ValueTask.FromResult(false);
        }
    }

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

    public bool UsesGainValue { get; private set; }

    public bool UsesGainMode => false;

    public bool UsesOffsetValue { get; private set; }

    public bool UsesOffsetMode => true;

    public double PixelSizeX { get; private set; } = double.NaN;

    public double PixelSizeY { get; private set; } = double.NaN;

    public short MaxBinX { get; private set; }

    public short MaxBinY { get; private set; }

    public int BinX
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            return _cameraSettings.BinX;
        }

        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                _cameraSettings = _cameraSettings with { BinX = (byte)value };
            }
        }
    }

    public int BinY
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            return _cameraSettings.BinY;
        }

        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                _cameraSettings = _cameraSettings with { BinY = (byte)value };
            }
        }
    }

    public int StartX
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            return _cameraSettings.StartX;
        }

        set
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (value >= 0 && value * BinX < CameraXSize)
            {
                _cameraSettings = _cameraSettings with { StartX = value };
            }
            else
            {
                throw new ZWODriverException(ASI_ERROR_OUTOF_BOUNDARY, "StartX must be between 0 and Camera size (binned)");
            }
        }
    }

    public int StartY
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }

            return _cameraSettings.StartY;
        }

        set
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (value >= 0 && value * BinY < CameraYSize)
            {
                _cameraSettings = _cameraSettings with { StartY = value };
            }
            else
            {
                throw new ZWODriverException(ASI_ERROR_OUTOF_BOUNDARY, "StartY must be between 0 and Camera size (binned)");
            }
        }
    }

    public int NumX
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            
            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (value >= 1 && value * BinX < CameraXSize)
            {
                _cameraSettings = _cameraSettings with { Width = value };
            }
            else
            {
                throw new ZWODriverException(ASI_ERROR_OUTOF_BOUNDARY, "Width must be between 1 and Camera size (binned)");
            }
        }
    }

    public int NumY
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (value >= 1 && value * BinY < CameraYSize)
            {
                _cameraSettings = _cameraSettings with { Height = value };
            }
            else
            {
                throw new ZWODriverException(ASI_ERROR_OUTOF_BOUNDARY, "Height must be between 1 and Camera size (binned)");
            }
        }
    }

    public int CameraXSize { get; private set; } = int.MinValue;

    public int CameraYSize { get; private set; } = int.MinValue;

    /// <summary>
    /// TODO: implement trigger
    /// </summary>
    public string? ReadoutMode
    {
        get => null;
        set { }
    }

    public bool FastReadout
    {
        get => Connected && CanFastReadout && _cameraSettings.FastReadout;
        set
        {
            if (Connected && CanFastReadout)
            {
                _cameraSettings = _cameraSettings with { FastReadout = value };
            }
        }
    }

    public Float32HxWImageData? ImageData
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

    Float32HxWImageData DownloadImage(in CameraSettings exposureSettings)
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

        var dataAfterExpErrorCode = ASIGetDataAfterExp(ConnectionId, nativeBuffer.Pointer, expBufferSize);
        if (dataAfterExpErrorCode is not ASI_SUCCESS)
        {
            throw new InvalidOperationException($"Getting data after exposure returned {dataAfterExpErrorCode} w={w} h={h} bit={exposureSettings.BitDepth}");
        }

        var cachedArray = Interlocked.Exchange(ref _camImageArray, null);
        var (data, maxValue) = cachedArray?.Data is null || cachedArray.Data.GetLength(0) != h || cachedArray.Data.GetLength(1) != w
            ? new Float32HxWImageData(new float[h, w], 0f)
            : cachedArray;

        switch (exposureSettings.BitDepth.BitSize())
        {
            case 8:
                var bytes = new byte[w * h];
                Marshal.Copy(nativeBuffer.Pointer, bytes, 0, bytes.Length);
                for (var i = 0; i < h; i++)
                {
                    for (var j = 0; j < w; j++)
                    {
                        maxValue = MathF.Max(data[i, j] = bytes[(w * i) + j], maxValue);
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
                        maxValue = MathF.Max(data[i, j] = shorts[(w * i) + j], maxValue);
                    }
                }
                break;

            default:
                throw new InvalidOperationException($"Cannot handle bit depth {exposureSettings.BitDepth}");
        }

        // put the new array back
        var array = new Float32HxWImageData(data, maxValue);
        _ = Interlocked.CompareExchange(ref _camImageArray, array, null);
        // finished downloading
        _camState = CameraState.Idle;

        return array;
    }

    public bool ImageReady
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (CameraState is CameraState.Error)
            {
                return false;
            }

            var isReady = IMAGE_STATE_NO_IMG != Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_NO_IMG, IMAGE_STATE_NO_IMG);

            return isReady;
        }
    }

    public bool IsPulseGuiding => Interlocked.CompareExchange(ref _pulseGuideDirections, 0, 0) is not 0;

    public bool CoolerOn
    {
        get => Connected
            && CanGetCoolerOn
            && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_ON, out var isOn, out _) is ASI_SUCCESS
            && isOn == Convert.ToInt32(true);

        set
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (!CanSetCoolerOn)
            {
                throw new ZWODriverException(ASI_ERROR_GENERAL_ERROR, "Cooler on is not supported");
            }
            else if (ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_ON, Convert.ToInt32(value)) is var code and not ASI_SUCCESS)
            {
                throw new ZWODriverException(code, $"Failed to turn cooler {(value ? "on" : "off")}");
            }
        }
    }

    public double CoolerPower
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (!CanGetCoolerPower)
            {
                throw new ZWODriverException(ASI_ERROR_GENERAL_ERROR, "Getting cooler power on is not supported");
            }
            else if (ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC, out var percentage, out _) is var code and not ASI_SUCCESS)
            {
                throw new ZWODriverException(code, "Failed to get cooler power");
            }
            else
            {
                return percentage;
            }
        }
    }

    public double SetCCDTemperature
    {
        get
        {
            if (!Connected)
            {
                throw new ZWODriverException(ASI_ERROR_CAMERA_CLOSED, "Camera is not connected");
            }
            else if (!CanSetCCDTemperature)
            {
                throw new ZWODriverException(ASI_ERROR_GENERAL_ERROR, "Cooler set CCD temp is not supported");
            }
            else if (ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, out var val, out _) is var code and not ASI_SUCCESS)
            {
                throw new ZWODriverException(code, "Failed to get CCD temperature");
            }
            else
            {
                return val;
            }
        }

        set
        {
            // TODO exception
            if (Connected
                && CanSetCCDTemperature
                && TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, out var min, out var max)
                && value >= min
                && value <= max
            )
            {
                ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, (int)value);
            }
        }
    }

    public double HeatSinkTemperature { get; } = double.NaN;

    public double CCDTemperature
        => ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_TEMPERATURE, out var intTemp, out _) is ASI_SUCCESS ? intTemp * 0.1d : double.NaN;

    public BitDepth? BitDepth
    {
        get => Connected ? _cameraSettings.BitDepth : null;
        set
        {
            if (Connected && value is { } bitDepth && _supportedBitDepth.Contains(bitDepth))
            {
                _cameraSettings = _cameraSettings with { BitDepth = bitDepth };
            }
        }
    }

    public short Gain
    {
        get
        {
            if (Connected
                && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_GAIN, out var gain, out _) is ASI_SUCCESS
                && gain >= GainMin
                && gain <= GainMax
            )
            {
                return (short)gain;
            }
            return short.MinValue;
        }

        set
        {
            if (value < GainMin || value > GainMax || ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_GAIN, value) is not ASI_SUCCESS)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"{nameof(Gain)} must be between {GainMin} and {GainMax} inclusive");
            }
        }
    }

    public short GainMin { get; private set; } = short.MinValue;

    public short GainMax { get; private set; } = short.MinValue;

    public IReadOnlyList<string> Gains => throw new InvalidOperationException($"{nameof(Gains)} is not supported");

    public int Offset
    {
        get
        {
            // TODO exception
            if (Connected
                && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_BRIGHTNESS, out var offset, out _) is ASI_SUCCESS
                && offset >= OffsetMin
                && offset <= OffsetMax
            )
            {
                return offset;
            }
            return int.MinValue;
        }

        set
        {
            if (value < OffsetMin || value > OffsetMax || ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_GAIN, value) is not ASI_SUCCESS)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"{nameof(Offset)} must be between {OffsetMin} and {OffsetMax} inclusive");
            }
        }
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IReadOnlyList<string> Offsets => throw new InvalidOperationException($"{nameof(Offsets)} is not supported");

    public DateTimeOffset? LastExposureStartTime => _exposureData?.StartTime;

    public TimeSpan? LastExposureDuration => _exposureData?.ActualDuration;

    public FrameType LastExposureFrameType => _exposureData?.FrameType ?? FrameType.None;

    public SensorType SensorType { get; private set; }

    public int BayerOffsetX { get; private set; } = int.MinValue;

    public int BayerOffsetY { get; private set; } = int.MinValue;

    public CameraState CameraState
    {
        get
        {
            if (_camState is CameraState.Exposing && ASIGetExpStatus(ConnectionId, out var snapStatus) is ASI_SUCCESS)
            {
                switch (snapStatus)
                {
                    case ASI_EXPOSURE_STATUS.ASI_EXP_IDLE:
                    case ASI_EXPOSURE_STATUS.ASI_EXP_FAILED:
                        _camState = CameraState.Idle;
                        Interlocked.Exchange(ref _camImageReady, IMAGE_STATE_NO_IMG);
                        break;

                    case ASI_EXPOSURE_STATUS.ASI_EXP_SUCCESS:
                        _camState = CameraState.Idle;
                        // do not provide the actual time as it is not clear how long ago it finished
                        SetImageReadyToDownload(null);
                        break;

                    case ASI_EXPOSURE_STATUS.ASI_EXP_WORKING:
                        _camState = CameraState.Exposing;
                        break;
                }
            }

            return _camState;
        }
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

    public double ExposureResolution { get; } = 1E-06;

    public int MaxADU
    {
        get
        {
            if (Connected && _cameraSettings.BitDepth.IsIntegral() && _cameraSettings.BitDepth.BitSize() is { } bitSize and > 0)
            {
                return bitSize switch
                {
                    8 => byte.MaxValue,
                    // return true ADC size if available
                    16 => BitDepthEx.FromValue(ADCBitDepth) is { } adcBitDepth && adcBitDepth.MaxIntValue() is { } maxADCBitDepth ? maxADCBitDepth : ushort.MaxValue,
                    _ => int.MinValue
                };
            }
            return int.MinValue;
        }
    }

    public double ElectronsPerADU { get; private set; } = double.NaN;

    public double FullWellCapacity => ElectronsPerADU * MaxADU;

    public void AbortExposure() => StopExposure();

    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType)
    {
        var settingsSnapshot = _cameraSettings;

        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration), duration, "0.0 upwards");

        int durationInNanoSecs;
        if (TryGetControlRange(ConnectionId, ASI_CONTROL_TYPE.ASI_EXPOSURE, out var min, out var max))
        {
            durationInNanoSecs = Math.Min(max, Math.Max(min, (int)Math.Round(duration.TotalMilliseconds * 1000)));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Could not find min,max");
        }

        var getROIErrorCode = ASIGetROIFormat(ConnectionId, out var currentWidth, out var currentHeight, out var currentBin, out var currentImgType);
        var getStartXYErrorCode = ASIGetStartPos(ConnectionId, out var currentStartX, out var currentStartY);

        // check if any parameters that require stopping expore changed
        bool bitDepthChanged;
        if (getROIErrorCode is ASI_SUCCESS && getStartXYErrorCode is ASI_SUCCESS)
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
                StopExposure();

                var setROIErrorCode = ASISetROIFormat(ConnectionId, settingsSnapshot.Width, settingsSnapshot.Height, BinX, settingsSnapshot.BitDepth.ToASIImageType());
                var setStartXYErrorCode = ASISetStartPos(ConnectionId, settingsSnapshot.StartX, settingsSnapshot.StartY);
                if (setROIErrorCode is not ASI_SUCCESS)
                {
                    _camState = CameraState.Error;
                    throw new ZWODriverException(setROIErrorCode, $"Failed to set ROI format: {settingsSnapshot}");
                }
                else if (setStartXYErrorCode is not ASI_SUCCESS)
                {
                    _camState = CameraState.Error;
                    throw new ZWODriverException(setStartXYErrorCode, $"Failed to set X-Y offset of ROI to x={settingsSnapshot.StartX}, y={settingsSnapshot.StartY}");
                }
            }
        }
        else if (getROIErrorCode is not ASI_SUCCESS)
        {
            _camState = CameraState.Error;

            throw new ZWODriverException(getROIErrorCode, "Failed to retrieve current ROI format");
        }
        else
        {
            _camState = CameraState.Error;

            throw new ZWODriverException(getStartXYErrorCode,"Failed to retrieve X-Y offset of ROI");
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
        var getExposureErrorCode = ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_EXPOSURE, out int currentExposure, out _);
        if (getExposureErrorCode is ASI_SUCCESS)
        {
            if (currentExposure != durationInNanoSecs)
            {
                var setExposureErrorCode = ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_EXPOSURE, durationInNanoSecs);
                if (setExposureErrorCode is not ASI_SUCCESS)
                {
                    _camState = CameraState.Error;
                    throw new ZWODriverException(setExposureErrorCode, $"Failed to set exposure to {durationInNanoSecs} ns");
                }
            }
        }
        else
        {
            _camState = CameraState.Error;
            throw new ZWODriverException(getExposureErrorCode, "Failed to retrieve current exposure settings");
        }

        var startExposureErrorCode = ASIStartExposure(ConnectionId, frameType.NeedsOpenShutter() ? ASI_FALSE : ASI_TRUE);
        if (startExposureErrorCode is ASI_SUCCESS)
        {
            _camState = CameraState.Exposing;
            var startTime = External.TimeProvider.GetUtcNow();
            _exposureData = new ExposureData(startTime, duration, null, frameType, Gain, Offset);
            // ensure that on image readout we use the settings that the image was exposed with
            _exposureSettings = settingsSnapshot;
            Interlocked.Exchange(ref _camImageReady, IMAGE_STATE_NO_IMG);

            return startTime;
        }
        else
        {
            _camState = CameraState.Error;
            throw new ZWODriverException(startExposureErrorCode, $"Failed to start exposure frame type={frameType} duration={durationInNanoSecs} ns");
        }
    }

    public void PulseGuide(GuideDirection guideDirection, TimeSpan duration)
    {
        var asiGuideDirection = guideDirection switch
        {
            GuideDirection.West => ASI_GUIDE_DIRECTION.ASI_GUIDE_WEST,
            GuideDirection.North => ASI_GUIDE_DIRECTION.ASI_GUIDE_NORTH,
            GuideDirection.East => ASI_GUIDE_DIRECTION.ASI_GUIDE_EAST,
            GuideDirection.South => ASI_GUIDE_DIRECTION.ASI_GUIDE_SOUTH,
            var invalid => throw new ArgumentException($"Invalid guide direction {invalid}", nameof(guideDirection))
        };

        var timer = External.TimeProvider.CreateTimer(StopPulseGuiding, asiGuideDirection, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (ASIPulseGuideOn(ConnectionId, asiGuideDirection) is var code and not ASI_SUCCESS)
        {
            throw new ZWODriverException(code, $"Failed to pulse guide {guideDirection} for {duration:o}");
        }
        else
        {
            UpdateGuideDirections(asiGuideDirection, (existing, bit) => existing | bit);

            Interlocked.Exchange(ref _pulseGuiderTimers[(int)asiGuideDirection], timer)?.Dispose();
            timer.Change(duration, Timeout.InfiniteTimeSpan);
        }
    }

    private void UpdateGuideDirections(ASI_GUIDE_DIRECTION asiGuideDirection, Func<int, int, int> updateFunc)
    {
        var dirAsInt = (int)asiGuideDirection;
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
        if (obj is ASI_GUIDE_DIRECTION asiGuideDirection)
        {
            if (ASIPulseGuideOff(ConnectionId, asiGuideDirection) is var code and not ASI_SUCCESS)
            {
                External.AppLogger.LogError("Failed to stop guiding in direction {GuideDirection} due to error: {ErrorCode}", asiGuideDirection, code);
            }
            else
            {
                UpdateGuideDirections(asiGuideDirection, (existing, bit) => existing & ~bit);

                Interlocked.Exchange(ref _pulseGuiderTimers[(int)asiGuideDirection], null)?.Dispose();
            }
        }
        else
        {
            External.AppLogger.LogCritical("Invalid state: {obj} in stop pulse guiding callback", obj);
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
            throw new ZWODriverException(ASI_ERROR_BUFFER_TOO_SMALL, $"Buffer size {bufferSize} is not large enough");
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

    static int CalculateBufferSize(in CameraSettings settings) => settings.BitDepth.BitSize() / 8 * settings.Width * settings.Height;

    public void StopExposure()
    {
        if (_camState == CameraState.Idle)
        {
            return;
        }

        if (ASIStopExposure(ConnectionId) is ASI_SUCCESS)
        {
            _camState = CameraState.Idle;
            SetImageReadyToDownload(_exposureData is { } data ? External.TimeProvider.GetUtcNow() - data.StartTime : null);
        }
    }

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int FocusPosition { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;
 
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public Target? Target { get; set; }
    #endregion
}
