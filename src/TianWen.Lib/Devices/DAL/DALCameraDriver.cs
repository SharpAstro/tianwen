using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    public DALCameraDriver(TDevice device, IExternal external) : base(device, external)
    {
        DeviceConnectedEvent += DALCameraDriver_DeviceConnectedEvent;
    }

    private int ADCBitDepth { get; set; } = int.MinValue;

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

    public double HeatSinkTemperature { get; } = double.NaN;

    public int CameraXSize { get; private set; } = int.MinValue;

    public int CameraYSize { get; private set; } = int.MinValue;

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
                throw NotConnectedException();
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

            return _cameraSettings.Height;
        }

        set
        {
            if (!Connected)
            {
                throw NotConnectedException();
            }
            else if (value >= 1 && value * BinX < CameraXSize)
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
            else if (value >= 1 && value * BinY < CameraYSize)
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

    public SensorType SensorType { get; private set; }

    public int BayerOffsetX { get; private set; } = int.MinValue;

    public int BayerOffsetY { get; private set; } = int.MinValue;

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

    public bool ImageReady
    {
        get
        {
            if (!Connected)
            {
                throw NotConnectedException();
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
            && _deviceInfo.GetControlValue(CMOSControlType.CoolerOn, out var isOn, out _) is CMOSErrorCode.Success
            && isOn == Convert.ToInt32(true);

        set
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
        }
    }

    public double CoolerPower
    {
        get
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
                throw NotConnectedException();
            }
            else if (!CanSetCCDTemperature)
            {
                throw OperationalException(CMOSErrorCode.GeneralError, "Cooler set CCD temp is not supported");
            }
            else if (_deviceInfo.GetControlValue(CMOSControlType.TargetTemperature, out var val, out _) is var code and not CMOSErrorCode.Success)
            {
                throw OperationalException(code, "Failed to get CCD temperature");
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
                && _deviceInfo.TryGetControlRange(CMOSControlType.TargetTemperature, out var min, out var max)
                && value >= min
                && value <= max
            )
            {
                _deviceInfo.SetControlValue(CMOSControlType.TargetTemperature, (int)value);
            }
        }
    }

    public double CCDTemperature
        => _deviceInfo.GetControlValue(CMOSControlType.TemperatureDeci, out var intTemp, out _) is CMOSErrorCode.Success ? intTemp * 0.1d : double.NaN;

    public short Gain
    {
        get
        {
            if (Connected
                && _deviceInfo.GetControlValue(CMOSControlType.Gain, out var gain, out _) is CMOSErrorCode.Success
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
            if (value < GainMin || value > GainMax || _deviceInfo.SetControlValue(CMOSControlType.Gain, value) is not CMOSErrorCode.Success)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"{nameof(Gain)} must be between {GainMin} and {GainMax} inclusive");
            }
        }
    }

    public IReadOnlyList<string> Gains => throw new InvalidOperationException($"{nameof(Gains)} is not supported");

    public int Offset
    {
        get
        {
            // TODO exception
            if (Connected
                && _deviceInfo.GetControlValue(CMOSControlType.Brightness, out var offset, out _) is CMOSErrorCode.Success
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
            if (value < OffsetMin || value > OffsetMax || _deviceInfo.SetControlValue(CMOSControlType.Brightness, value) is not CMOSErrorCode.Success)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"{nameof(Offset)} must be between {OffsetMin} and {OffsetMax} inclusive");
            }
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

        var highestPossibleBitDepth = _supportedBitDepth.Where(x => x.IsIntegral()).OrderByDescending(x => x.BitSize()).First();
        _cameraSettings = new CameraSettings(0, 0, CameraXSize  = _deviceInfo.MaxWidth, CameraYSize = _deviceInfo.MaxHeight, 1, 1, highestPossibleBitDepth, false);
        PixelSizeX = PixelSizeY = _deviceInfo.PixelSize;
        ElectronsPerADU = _deviceInfo.ElectronPerADU is var elecPerADU and > 0f ? elecPerADU : double.NaN;
        ADCBitDepth = _deviceInfo.BitDepth;

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

        var initControlValues = new Dictionary<CMOSControlType, int>
        {
            [CMOSControlType.Flip] = 0,
            [CMOSControlType.Gamma] = 50,
            [CMOSControlType.HighSpeedMode] = Convert.ToInt32(_cameraSettings.FastReadout),
            [CMOSControlType.MonoBin] = 0,
            [CMOSControlType.HardwareBin] = 0,
            [CMOSControlType.WB_R] = 50,
            [CMOSControlType.WB_B] = 50,
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
    }

    public CameraState CameraState
    {
        get
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

        var dataAfterExpErrorCode = _deviceInfo.GetDataAfterExposure(nativeBuffer.Pointer, expBufferSize);
        if (dataAfterExpErrorCode is not CMOSErrorCode.Success)
        {
            throw new InvalidOperationException($"Getting data after exposure returned {dataAfterExpErrorCode} w={w} h={h} bit={exposureSettings.BitDepth}");
        }

        var cachedArray = Interlocked.Exchange(ref _camImageArray, null);
        var (data, maxValue) = cachedArray?.Data is null || cachedArray.Data.GetLength(0) != h || cachedArray.Data.GetLength(1) != w
            ? new Float32HxWImageData(new float[1, h, w], 0f)
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
                        maxValue = MathF.Max(data[0, i, j] = bytes[(w * i) + j], maxValue);
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
                        maxValue = MathF.Max(data[0, i, j] = shorts[(w * i) + j], maxValue);
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

    public void AbortExposure() => StopExposure();


    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType)
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
                StopExposure();

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

        var startExposureErrorCode = frameType.NeedsOpenShutter()
            ? _deviceInfo.StartLightExposure()
            : _deviceInfo.StartDarkExposure();
        if (startExposureErrorCode is CMOSErrorCode.Success)
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
            throw OperationalException(startExposureErrorCode, $"Failed to start exposure frame type={frameType} duration={durationInNanoSecs} ns");
        }
    }

    public void PulseGuide(GuideDirection guideDirection, TimeSpan duration)
    {
        var timer = External.TimeProvider.CreateTimer(StopPulseGuiding, guideDirection, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_deviceInfo.PulseGuideOn(guideDirection) is var code and not CMOSErrorCode.Success)
        {
            throw OperationalException(code, $"Failed to pulse guide {guideDirection} for {duration:o}");
        }
        else
        {
            UpdateGuideDirections(guideDirection, (existing, bit) => existing | bit);

            Interlocked.Exchange(ref _pulseGuiderTimers[(int)guideDirection], timer)?.Dispose();
            timer.Change(duration, Timeout.InfiniteTimeSpan);
        }
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
            if (_deviceInfo.PulseGuideOff(guideDirection) is var code and not CMOSErrorCode.Success)
            {
                External.AppLogger.LogError("Failed to stop guiding in direction {GuideDirection} due to error: {ErrorCode}", guideDirection, code);
            }
            else
            {
                UpdateGuideDirections(guideDirection, (existing, bit) => existing & ~bit);

                Interlocked.Exchange(ref _pulseGuiderTimers[(int)guideDirection], null)?.Dispose();
            }
        }
        else
        {
            External.AppLogger.LogCritical("Invalid state: {obj} in stop pulse guiding callback", obj);
        }
    }

    public void StopExposure()
    {
        if (_camState == CameraState.Idle)
        {
            return;
        }

        if (_deviceInfo.StopExposure() is CMOSErrorCode.Success)
        {
            Interlocked.Exchange(ref _camState, CameraState.Idle);
            SetImageReadyToDownload(_exposureData is { } data ? External.TimeProvider.GetUtcNow() - data.StartTime : null);
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

    static int CalculateBufferSize(in CameraSettings settings) => settings.BitDepth.BitSize() / 8 * settings.Width * settings.Height;

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
