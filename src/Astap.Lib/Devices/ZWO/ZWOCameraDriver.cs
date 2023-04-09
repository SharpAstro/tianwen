using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using ZWOptical.ASISDK;
using static ZWOptical.ASISDK.ASICameraDll2;
using static ZWOptical.ASISDK.ASICameraDll2.ASI_BOOL;
using static ZWOptical.ASISDK.ASICameraDll2.ASI_ERROR_CODE;

namespace Astap.Lib.Devices.ZWO;

public class ZWOCameraDriver : ZWODeviceDriverBase<ASI_CAMERA_INFO>, ICameraDriver
{
    const int IMAGE_STATE_NO_IMG = 0;
    const int IMAGE_STATE_READY_TO_DOWNLOAD = 1;
    const int IMAGE_STATE_DOWNLOADED = 2;

    private ExposureSettings _cameraSettings;
    private ExposureSettings _exposureSettings;
    private IReadOnlySet<BitDepth> _supportedBitDepth = ImmutableHashSet.Create<BitDepth>();

    /// <summary>
    /// If fast readout is true, then high speed mode will be enabled on next exposure.
    /// </summary>
    private volatile bool _fastReadout = false;

    /// <summary>
    /// Holds currently connected camera info
    /// </summary>
    private ASICameraDll2.ASI_CAMERA_INFO _camInfo;

    /// <summary>
    /// Camera state
    /// </summary>
    private volatile CameraState _camState = CameraState.Idle;

    /// <summary>
    /// Holds a native (COM) buffer that can be filled by the native ASI SDK.
    /// </summary>
    private IntPtr _nativeBuffer;

    /// <summary>
    /// Size of the <see cref="_nativeBuffer"/>. Non-positive size means no buffer allocated.
    /// </summary>
    private int _nativeBufferSize = -1;

    // Initialise variables to hold values required for functionality tested by Conform

    private DateTime _exposureStart = DateTime.MinValue;
    private double? _camLastExposureDuration;
    private int _camImageReady = 0;
    private int[,]? _camImageArray;

    public ZWOCameraDriver(ZWODevice device) : base(device)
    {
        DeviceConnectedEvent += ZWOCameraDriver_DeviceConnectedEvent;
    }

    private void ZWOCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            ProcessDeviceInfo((in ASI_CAMERA_INFO camInfo) =>
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

                _cameraSettings = new(0, 0, CameraXSize  = camInfo.MaxWidth, CameraYSize = camInfo.MaxHeight, 1, Devices.BitDepth.Int8);
                PixelSizeX = PixelSizeY = camInfo.PixelSize;
                ElectronsPerADU = camInfo.ElecPerADU is var elecPerADU and > 0f ? elecPerADU : double.NaN;
                ADCBitDepth = camInfo.BitDepth;

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

                var initControlValues = new Dictionary<ASI_CONTROL_TYPE, int>
                {
                    [ASI_CONTROL_TYPE.ASI_FLIP] = 0,
                    [ASI_CONTROL_TYPE.ASI_GAMMA] = 50,
                    [ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE] = 0,
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
            });
            // TODO initalise stuff
        }
    }

    private int ADCBitDepth { get; set; } = int.MinValue;

    public override string? DriverVersion => ASIGetSDKVersion();

    public override string? Description => "ZWO Camera driver using C# SDK wrapper";

    protected override bool ConnectDevice(out int connectionId, out ASI_CAMERA_INFO camInfo)
    {
        var deviceId = _device.DeviceId;
        if (
            (TryFindCameraBySerial(deviceId, out var camId, out camInfo)
            || TryFindCameraByID(deviceId, out camId, out camInfo)
            || TryFindCameraByName(deviceId, out camId, out camInfo)
            )
            && ASIInitCamera(camId.Value) is ASI_SUCCESS
        )
        {
            connectionId = camId.Value;
            return true;
        }

        connectionId = int.MinValue;
        return false;
    }

    protected override bool DisconnectDevice(int connectionId) => ASICloseCamera(connectionId) is ASI_ERROR_CODE.ASI_SUCCESS;

    static bool TryFindCameraBySerial(string deviceId, [NotNullWhen(true)] out int? camId, out ASI_CAMERA_INFO camInfo)
    {
        var count = ASIGetNumOfConnectedCameras();
        for (var i = 0; i < count; i++)
        {
            if (ASIGetCameraProperty(out camInfo, i) is ASI_SUCCESS
                && ASIOpenCamera(camInfo.CameraID) is ASI_SUCCESS
            )
            {
                if (ASIGetSerialNumber(camInfo.CameraID, out var camSerial) is ASI_SUCCESS && camSerial.ID == deviceId)
                {
                    camId = camInfo.CameraID;
                    return true;
                }
                else
                {
                    _ = ASICloseCamera(camInfo.CameraID);
                }
            }
        }

        camInfo = default;
        camId = null;
        return false;
    }

    static bool TryFindCameraByID(string deviceId, [NotNullWhen(true)] out int? camId, out ASI_CAMERA_INFO camInfo)
    {
        var count = ASIGetNumOfConnectedCameras();
        for (var i = 0; i < count; i++)
        {
            if (ASIGetCameraProperty(out camInfo, i) is ASI_SUCCESS
                && camInfo.IsUSB3Camera is ASI_TRUE
                && ASIOpenCamera(camInfo.CameraID) is ASI_SUCCESS
            )
            {
                if (ASIGetID(camInfo.CameraID, out var camSerial) is ASI_SUCCESS && camSerial.ID == deviceId)
                {
                    camId = camInfo.CameraID;
                    return true;
                }
                else
                {
                    _ = ASICloseCamera(camInfo.CameraID);
                }
            }
        }

        camInfo = default;
        camId = null;
        return false;
    }

    static bool TryFindCameraByName(string name, [NotNullWhen(true)] out int? camId, out ASI_CAMERA_INFO camInfo)
    {
        var count = ASIGetNumOfConnectedCameras();
        for (var i = 0; i < count; i++)
        {
            if (ASIGetCameraProperty(out camInfo, i) is ASI_SUCCESS
                && camInfo.Name == name
                && ASIOpenCamera(camInfo.CameraID) is ASI_SUCCESS
            )
            {
                camId = camInfo.CameraID;
                return true;
            }
        }

        camInfo = default;
        camId = null;
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

    public string? ReadoutMode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public bool FastReadout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public int[,]? ImageData => throw new NotImplementedException();

    public bool ImageReady => throw new NotImplementedException();

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

    public double SetCCDTemperature { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double HeatSinkTemperature => throw new InvalidOperationException($"{nameof(HeatSinkTemperature)} is not implemented!");

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

    public DateTime LastExposureStartTime => throw new NotImplementedException();

    public TimeSpan LastExposureDuration => throw new NotImplementedException();

    public SensorType SensorType => throw new NotImplementedException();

    public int BayerOffsetX => throw new NotImplementedException();

    public int BayerOffsetY => throw new NotImplementedException();

    public CameraState CameraState => throw new NotImplementedException();

    public ImageSourceFormat ImageSourceFormat { get; } = ImageSourceFormat.HeightXWidthLE;

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

    public void StartExposure(TimeSpan duration, bool light)
    {
        throw new NotImplementedException();
    }

    public void StopExposure()
    {
        throw new NotImplementedException();
    }

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int FocusPos { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;
    #endregion
}
