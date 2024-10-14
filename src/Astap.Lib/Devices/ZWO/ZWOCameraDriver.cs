using Astap.Lib.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static ZWOptical.SDK.ASICamera2;
using static ZWOptical.SDK.ASICamera2.ASI_BOOL;
using static ZWOptical.SDK.ASICamera2.ASI_ERROR_CODE;

namespace Astap.Lib.Devices.ZWO;

public class ZWOCameraDriver : ZWODeviceDriverBase<ASI_CAMERA_INFO>, ICameraDriver
{
    record class NativeBuffer(IntPtr Pointer, int Size);

    const int IMAGE_STATE_NO_IMG = 0;
    const int IMAGE_STATE_READY_TO_DOWNLOAD = 1;
    const int IMAGE_STATE_DOWNLOADED = 2;

    private ExposureSettings _cameraSettings;
    private ExposureSettings _exposureSettings;
    private IReadOnlySet<BitDepth> _supportedBitDepth = ImmutableHashSet.Create<BitDepth>();

    /// <summary>
    /// Camera state
    /// </summary>
    private volatile CameraState _camState = CameraState.Idle;

    /// <summary>
    /// Holds a native (COM) buffer that can be filled by the native ASI SDK.
    /// </summary>
    private NativeBuffer? _nativeBuffer;

    // Initialise variables to hold values required for functionality tested by Conform

    private DateTimeOffset? _lastExposureStartTime;
    private TimeSpan? _lastExposureDuration;
    private int _camImageReady = 0;
    private Float32HxWImageData? _camImageArray;
    private FrameType _frameType;

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
        _cameraSettings = new(0, 0, CameraXSize  = camInfo.MaxWidth, CameraYSize = camInfo.MaxHeight, 1, highestPossibleBitDepth, fastReadout: false);
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

    public override string? DriverVersion => ASIGetSDKVersion().ToString();

    public override string? Description => "ZWO Camera driver using C# SDK wrapper";

    protected override bool ConnectDevice(out int connectionId, out ASI_CAMERA_INFO camInfo)
    {
        if (base.ConnectDevice(out connectionId, out camInfo))
        {
            return ASIInitCamera(camInfo.ID) is ASI_SUCCESS;
        }

        return false;
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
        get => _cameraSettings.Bin;
        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                ExposureSettings.WithBin(ref _cameraSettings, (byte)value);
            }
        }
    }

    public int BinY
    {
        get => _cameraSettings.Bin;
        set
        {
            if (Connected && value >= 1 && value <= MaxBinX && value <= MaxBinY && value <= byte.MaxValue)
            {
                ExposureSettings.WithBin(ref _cameraSettings, (byte)value);
            }
        }
    }

    public int StartX
    {
        get => _cameraSettings.StartX;
        set
        {
            if (Connected && value >= 0 && value < CameraXSize)
            {
                ExposureSettings.WithStartX(ref _cameraSettings, value);
            }
        }
    }

    public int StartY
    {
        get => _cameraSettings.StartY;
        set
        {
            if (Connected && value >= 0 && value < CameraYSize)
            {
                ExposureSettings.WithStartY(ref _cameraSettings, value);
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
                ExposureSettings.WithFastReadout(ref  _cameraSettings, value);
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
                    var exposureSettings = _exposureSettings;
                    _camState = CameraState.Download;

                    return DownloadImage(exposureSettings);
            }

            return _camImageArray;
        }
    }

    Float32HxWImageData DownloadImage(in ExposureSettings exposureSettings)
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
                return false;
            }
            else if (CameraState is CameraState.Error)
            {
                return false;
            }

            var isReady = IMAGE_STATE_NO_IMG != Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_NO_IMG, IMAGE_STATE_NO_IMG);

            return isReady;
        }
    }

    public bool CoolerOn
    {
        get => Connected
            && CanGetCoolerOn
            && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_ON, out var isOn, out _) is ASI_SUCCESS
            && isOn == Convert.ToInt32(true);

        set
        {
            if (Connected && CanSetCoolerOn)
            {
                _ = ASISetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_ON, Convert.ToInt32(value));
            }
        }
    }

    public double CoolerPower => Connected
        && CanGetCoolerPower
        && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_COOLER_POWER_PERC, out var percentage, out _) is ASI_SUCCESS
            ? percentage
            : double.NaN;

    public double SetCCDTemperature
    {
        get => Connected
            && CanSetCCDTemperature
            && ASIGetControlValue(ConnectionId, ASI_CONTROL_TYPE.ASI_TARGET_TEMP, out var val, out _) is ASI_SUCCESS
                ? val
                : double.NaN;

        set
        {
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
                ExposureSettings.WithBitDepth(ref _cameraSettings, bitDepth);
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

    public IEnumerable<string> Gains => throw new InvalidOperationException($"{nameof(Gains)} is not supported");

    public int Offset
    {
        get
        {
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

    public IEnumerable<string> Offsets => throw new InvalidOperationException($"{nameof(Offsets)} is not supported");

    public DateTimeOffset? LastExposureStartTime => _lastExposureStartTime;

    public TimeSpan? LastExposureDuration => _lastExposureDuration;

    public FrameType LastExposureFrameType => _frameType;

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
                        Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_READY_TO_DOWNLOAD, IMAGE_STATE_NO_IMG);
                        break;

                    case ASI_EXPOSURE_STATUS.ASI_EXP_WORKING:
                        _camState = CameraState.Exposing;
                        break;
                }
            }

            return _camState;
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
                    16 => BitDepthEx.FromValue(ADCBitDepth) is { } adcBitDepth ? adcBitDepth.MaxIntValue() : ushort.MaxValue,
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
            durationInNanoSecs = Math.Min(max, Math.Max(min, (int)(duration.TotalMilliseconds * 1000)));
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
                || currentStartY != settingsSnapshot.StartY)
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
            _lastExposureDuration = duration;
            var startTime = External.TimeProvider.GetLocalNow();
            _lastExposureStartTime = startTime;
            _frameType = frameType;
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

    private void DisposeNativeBuffer()
    {
        var existingBuffer = Interlocked.Exchange(ref _nativeBuffer, null);

        if (existingBuffer is not null && existingBuffer.Pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(existingBuffer.Pointer);
        }
    }

    protected override void DisposeNative() => DisposeNativeBuffer();

    static int CalculateBufferSize(in ExposureSettings settings) => settings.BitDepth.BitSize() / 8 * settings.Width * settings.Height;

    public void StopExposure()
    {
        if (_camState == CameraState.Idle)
        {
            return;
        }

        if (ASIStopExposure(ConnectionId) is ASI_SUCCESS)
        {
            Interlocked.CompareExchange(ref _camImageReady, IMAGE_STATE_READY_TO_DOWNLOAD, IMAGE_STATE_NO_IMG);
            _camState = CameraState.Idle;
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
