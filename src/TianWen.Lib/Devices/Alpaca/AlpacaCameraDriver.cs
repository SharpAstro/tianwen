using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaCameraDriver(AlpacaDevice device, IExternal external)
    : AlpacaDeviceDriverBase(device, external), ICameraDriver
{
    // Cached static properties (set once during InitDeviceAsync)
    private double _pixelSizeX, _pixelSizeY;
    private short _maxBinX, _maxBinY;
    private int _cameraXSize, _cameraYSize;
    private int _maxADU;
    private double _fullWellCapacity, _electronsPerADU, _exposureResolution;
    private SensorType _sensorType;
    private int _bayerOffsetX, _bayerOffsetY;

    // Write-through cached properties (set locally, PUT async on write)
    private int _binX = 1, _binY = 1;
    private int _startX, _startY;
    private int _numX, _numY;
    private short _gain;
    private int _offset;
    private bool _fastReadout;
    private bool _coolerOn;
    private double _setCCDTemperature;

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        CanGetCoolerPower = await TryGetCapabilityAsync("cangetcoolerpower", cancellationToken);
        CanSetCCDTemperature = await TryGetCapabilityAsync("cansetccdtemperature", cancellationToken);
        CanStopExposure = await TryGetCapabilityAsync("canstopexposure", cancellationToken);
        CanAbortExposure = await TryGetCapabilityAsync("canabortexposure", cancellationToken);
        CanFastReadout = await TryGetCapabilityAsync("canfastreadout", cancellationToken);
        CanPulseGuide = await TryGetCapabilityAsync("canpulseguide", cancellationToken);

        try
        {
            _coolerOn = await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "cooleron", cancellationToken);
            CanGetCoolerOn = true;
            CanSetCoolerOn = true;
        }
        catch
        {
            CanGetCoolerOn = false;
            CanSetCoolerOn = false;
        }

        CanGetHeatsinkTemperature = await TryGetTemperatureAsync("heatsinktemperature", cancellationToken) is not double.NaN;
        CanGetCCDTemperature = await TryGetTemperatureAsync("ccdtemperature", cancellationToken) is not double.NaN;

        // Cache static properties
        _pixelSizeX = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "pixelsizex", cancellationToken);
        _pixelSizeY = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "pixelsizey", cancellationToken);
        _maxBinX = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxbinx", cancellationToken);
        _maxBinY = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxbiny", cancellationToken);
        _cameraXSize = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "cameraxsize", cancellationToken);
        _cameraYSize = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "cameraysize", cancellationToken);
        _maxADU = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxadu", cancellationToken);
        _fullWellCapacity = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "fullwellcapacity", cancellationToken);
        _electronsPerADU = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "electronsperadu", cancellationToken);
        _exposureResolution = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "exposureresolution", cancellationToken);
        _sensorType = (SensorType)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "sensortype", cancellationToken);
        _bayerOffsetX = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "bayeroffsetx", cancellationToken);
        _bayerOffsetY = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "bayeroffsety", cancellationToken);

        // Cache initial write-through values
        _binX = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "binx", cancellationToken);
        _binY = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "biny", cancellationToken);
        _startX = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "startx", cancellationToken);
        _startY = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "starty", cancellationToken);
        _numX = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "numx", cancellationToken);
        _numY = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "numy", cancellationToken);

        if (CanSetCCDTemperature)
        {
            try { _setCCDTemperature = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "setccdtemperature", cancellationToken); } catch { }
        }

        var interfaceVersion = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "interfaceversion", cancellationToken);

        if (interfaceVersion >= 3)
        {
            try
            {
                _offset = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offset", cancellationToken);
                OffsetMin = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offsetmin", cancellationToken);
                OffsetMax = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offsetmax", cancellationToken);
                UsesOffsetValue = true;
            }
            catch
            {
                UsesOffsetValue = false;
                OffsetMin = int.MinValue;
                OffsetMax = int.MinValue;
            }

            if (!UsesOffsetValue)
            {
                try
                {
                    await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offset", cancellationToken);
                    await Client.GetStringAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offsets", cancellationToken);
                    UsesOffsetMode = true;
                }
                catch
                {
                    UsesOffsetMode = false;
                }
            }
        }

        try
        {
            _gain = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gain", cancellationToken);
            GainMin = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gainmin", cancellationToken);
            GainMax = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gainmax", cancellationToken);
            UsesGainValue = true;
        }
        catch
        {
            UsesGainValue = false;
            GainMin = short.MinValue;
            GainMax = short.MinValue;
        }

        if (!UsesGainValue)
        {
            try
            {
                await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gain", cancellationToken);
                await Client.GetStringAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gains", cancellationToken);
                UsesGainMode = true;
            }
            catch
            {
                UsesGainMode = false;
            }
        }

        return true;
    }

    private async Task<bool> TryGetCapabilityAsync(string endpoint, CancellationToken cancellationToken)
    {
        try { return await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, cancellationToken); }
        catch { return false; }
    }

    private async Task<double> TryGetTemperatureAsync(string endpoint, CancellationToken cancellationToken)
    {
        try { return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, cancellationToken); }
        catch { return double.NaN; }
    }

    // Capability properties (cached at init)
    public bool CanGetCoolerPower { get; private set; }
    public bool CanGetCoolerOn { get; private set; }
    public bool CanSetCoolerOn { get; private set; }
    public bool CanGetHeatsinkTemperature { get; private set; }
    public bool CanGetCCDTemperature { get; private set; }
    public bool CanSetCCDTemperature { get; private set; }
    public bool CanStopExposure { get; private set; }
    public bool CanAbortExposure { get; private set; }
    public bool CanFastReadout { get; private set; }
    public bool CanSetBitDepth { get; } = false;
    public bool CanPulseGuide { get; private set; }
    public bool UsesGainValue { get; private set; }
    public bool UsesGainMode { get; private set; }
    public bool UsesOffsetValue { get; private set; }
    public bool UsesOffsetMode { get; private set; }

    // Static properties (cached at init)
    public double PixelSizeX => _pixelSizeX;
    public double PixelSizeY => _pixelSizeY;
    public short MaxBinX => _maxBinX;
    public short MaxBinY => _maxBinY;
    public int CameraXSize => _cameraXSize;
    public int CameraYSize => _cameraYSize;
    public int MaxADU => _maxADU;
    public double FullWellCapacity => _fullWellCapacity;
    public double ElectronsPerADU => _electronsPerADU;
    public double ExposureResolution => _exposureResolution;
    public SensorType SensorType => _sensorType;
    public int BayerOffsetX => _bayerOffsetX;
    public int BayerOffsetY => _bayerOffsetY;

    // Write-through cached properties
    public int StartX
    {
        get => _startX;
        set { _startX = value; _ = PutPropertyAsync("startx", "StartX", value); }
    }

    public int StartY
    {
        get => _startY;
        set { _startY = value; _ = PutPropertyAsync("starty", "StartY", value); }
    }

    public int BinX
    {
        get => _binX;
        set { _binX = value; _ = PutPropertyAsync("binx", "BinX", value); }
    }

    public int BinY
    {
        get => _binY;
        set { _binY = value; _ = PutPropertyAsync("biny", "BinY", value); }
    }

    public int NumX
    {
        get => _numX;
        set { _numX = value; _ = PutPropertyAsync("numx", "NumX", value); }
    }

    public int NumY
    {
        get => _numY;
        set { _numY = value; _ = PutPropertyAsync("numy", "NumY", value); }
    }

    public int Offset
    {
        get => _offset;
        set { _offset = value; _ = PutPropertyAsync("offset", "Offset", value); }
    }

    public int OffsetMin { get; private set; }
    public int OffsetMax { get; private set; }
    public IReadOnlyList<string> Offsets => []; // TODO: parse string[] from Alpaca

    public short Gain
    {
        get => _gain;
        set { _gain = value; _ = PutPropertyAsync("gain", "Gain", (int)value); }
    }

    public short GainMin { get; private set; }
    public short GainMax { get; private set; }
    public IReadOnlyList<string> Gains => []; // TODO: parse string[] from Alpaca

    public bool FastReadout
    {
        get => _fastReadout;
        set { _fastReadout = value; _ = PutBoolPropertyAsync("fastreadout", "FastReadout", value); }
    }

    public string? ReadoutMode { get; set; } // Cached locally — ReadoutMode is index-based, local is sufficient

    public bool CoolerOn
    {
        get => _coolerOn;
        set { _coolerOn = value; _ = PutBoolPropertyAsync("cooleron", "CoolerOn", value); }
    }

    public double SetCCDTemperature
    {
        get => _setCCDTemperature;
        set { _setCCDTemperature = value; _ = PutDoublePropertyAsync("setccdtemperature", "SetCCDTemperature", value); }
    }

    // Dynamic properties — sync versions throw, callers should use async alternatives
    public double CoolerPower => throw new NotSupportedException("Use GetCoolerPowerAsync instead");
    public double HeatSinkTemperature => throw new NotSupportedException("Use GetHeatSinkTemperatureAsync instead");
    public double CCDTemperature => throw new NotSupportedException("Use GetCCDTemperatureAsync instead");
    public bool ImageReady => throw new NotSupportedException("Use GetImageReadyAsync instead");
    public bool IsPulseGuiding => throw new NotSupportedException("Use GetIsPulseGuidingAsync instead");
    public CameraState CameraState => throw new NotSupportedException("Use GetCameraStateAsync instead");

    // Async alternatives — native async HTTP calls
    public async ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
        => await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "imageready", cancellationToken);

    public async ValueTask<CameraState> GetCameraStateAsync(CancellationToken cancellationToken = default)
        => (CameraState)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "camerastate", cancellationToken);

    public async ValueTask<double> GetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "ccdtemperature", cancellationToken);

    public async ValueTask<double> GetHeatSinkTemperatureAsync(CancellationToken cancellationToken = default)
        => await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "heatsinktemperature", cancellationToken);

    public async ValueTask<double> GetCoolerPowerAsync(CancellationToken cancellationToken = default)
        => await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "coolerpower", cancellationToken);

    public async ValueTask<bool> GetCoolerOnAsync(CancellationToken cancellationToken = default)
    {
        _coolerOn = await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "cooleron", cancellationToken);
        return _coolerOn;
    }

    public async ValueTask SetCoolerOnAsync(bool value, CancellationToken cancellationToken = default)
    {
        await PutBoolPropertyAsync("cooleron", "CoolerOn", value);
        _coolerOn = value;
    }

    public async ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        await PutDoublePropertyAsync("setccdtemperature", "SetCCDTemperature", value);
        _setCCDTemperature = value;
    }

    public async ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "ispulseguiding", cancellationToken);

    public async ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        await PutMethodAsync("startexposure",
        [
            new("Duration", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)),
            new("Light", frameType.NeedsOpenShutter.ToString())
        ], cancellationToken);

        var startTime = External.TimeProvider.GetLocalNow();
        LastExposureStartTime = startTime;
        LastExposureFrameType = frameType;
        return startTime;
    }

    public async ValueTask StopExposureAsync(CancellationToken cancellationToken = default)
        => await PutMethodAsync("stopexposure", cancellationToken: cancellationToken);

    public async ValueTask AbortExposureAsync(CancellationToken cancellationToken = default)
        => await PutMethodAsync("abortexposure", cancellationToken: cancellationToken);

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var durationMs = (int)duration.Round(TimeSpanRoundingType.Millisecond).TotalMilliseconds;
        await PutMethodAsync("pulseguide",
        [
            new("Direction", ((int)direction).ToString(CultureInfo.InvariantCulture)),
            new("Duration", durationMs.ToString(CultureInfo.InvariantCulture))
        ], cancellationToken);
    }

    // Sync methods delegate to async — these are called from async contexts anyway
    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType)
        => throw new NotSupportedException("Use StartExposureAsync instead");

    public void StopExposure()
        => throw new NotSupportedException("Use StopExposureAsync instead");

    public void AbortExposure()
        => throw new NotSupportedException("Use AbortExposureAsync instead");

    public void PulseGuide(GuideDirection direction, TimeSpan duration)
        => throw new NotSupportedException("Use PulseGuideAsync instead");

    public Float32HxWImageData? ImageData => null; // TODO: Alpaca imagearray endpoint requires special binary handling

    public DateTimeOffset? LastExposureStartTime { get; private set; }

    public TimeSpan? LastExposureDuration => null; // TODO: requires async call to lastexposureduration

    public FrameType LastExposureFrameType { get; internal set; }

    public BitDepth? BitDepth
    {
        get
        {
            if (_maxADU <= 0) return null;
            int log2 = (int)MathF.Ceiling(MathF.Log(_maxADU) / MathF.Log(2.0f));
            int bitDepth = ((log2 + 7) / 8) * 8;
            return BitDepthEx.FromValue(bitDepth);
        }
        set => throw new InvalidOperationException("Setting bit depth is not supported!");
    }

    #region Write-through helpers
    private Task PutPropertyAsync(string endpoint, string paramName, int value)
        => Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, [new(paramName, value.ToString(CultureInfo.InvariantCulture))]);

    private Task PutBoolPropertyAsync(string endpoint, string paramName, bool value)
        => Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, [new(paramName, value.ToString())]);

    private Task PutDoublePropertyAsync(string endpoint, string paramName, double value)
        => Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, endpoint, [new(paramName, value.ToString(CultureInfo.InvariantCulture))]);
    #endregion

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
