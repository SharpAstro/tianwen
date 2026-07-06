using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaCameraDriver(AlpacaDevice device, IServiceProvider serviceProvider)
    : AlpacaDeviceDriverBase(device, serviceProvider), ICameraDriver
{
    // Cached static properties (set once during InitDeviceAsync)
    private double _pixelSizeX, _pixelSizeY;
    private short _maxBinX, _maxBinY;
    private int _cameraXSize, _cameraYSize;
    private int _maxADU;
    private double _fullWellCapacity, _electronsPerADU, _exposureResolution;
    private SensorType _sensorType;
    private int _bayerOffsetX, _bayerOffsetY;
    private string[] _gains = [];
    private string[] _offsets = [];
    private string[] _readoutModes = [];

    // Write-through cached properties (set locally, PUT async on write)
    private int _binX = 1, _binY = 1;
    private int _startX, _startY;
    private int _numX, _numY;
    private short _gain;
    private int _offset;
    private bool _fastReadout;
    private bool _coolerOn;
    private double _setCCDTemperature;
    private TimeSpan? _lastExposureDuration;

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

        // BayerOffsetX/Y exist only for Bayer-matrix sensors; monochrome and direct-colour sensors
        // throw PropertyNotImplemented per the ASCOM ICameraV3 spec (an unconditional read here
        // failed connect against a mono Alpaca camera), so read them only when applicable. The
        // fields default to 0.
        if (_sensorType is not SensorType.Monochrome and not SensorType.Color)
        {
            _bayerOffsetX = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "bayeroffsetx", cancellationToken);
            _bayerOffsetY = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "bayeroffsety", cancellationToken);
        }

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
                    // Offset-mode camera: "offset" is an index into the "offsets" string list. Read
                    // the list (an ARRAY endpoint -- GetStringAsync would mis-deserialize) so Offsets
                    // is populated and GetOffsetModeAsync can resolve the current index to a name.
                    _offsets = await Client.GetStringArrayAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offsets", cancellationToken) ?? [];
                    _offset = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "offset", cancellationToken);
                    UsesOffsetMode = _offsets.Length > 0;
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
                // Gain-mode camera: "gain" is an index into the "gains" string list. Read the list
                // (an ARRAY endpoint -- GetStringAsync would mis-deserialize) so Gains is populated
                // and GetGainModeAsync can resolve the current index to a name.
                _gains = await Client.GetStringArrayAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gains", cancellationToken) ?? [];
                _gain = (short)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "gain", cancellationToken);
                UsesGainMode = _gains.Length > 0;
            }
            catch
            {
                UsesGainMode = false;
            }
        }

        // Readout modes (ICameraV2+). Always available in principle, but gate defensively: a driver
        // that throws PropertyNotImplemented here must not fail connect (the mono-BayerOffset lesson).
        try
        {
            _readoutModes = await Client.GetStringArrayAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "readoutmodes", cancellationToken) ?? [];
        }
        catch
        {
            _readoutModes = [];
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

    public ValueTask<int> GetOffsetAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_offset);

    public async ValueTask SetOffsetAsync(int value, CancellationToken cancellationToken = default)
    {
        await PutPropertyAsync("offset", "Offset", value);
        _offset = value;
    }

    public int OffsetMin { get; private set; }
    public int OffsetMax { get; private set; }
    public IReadOnlyList<string> Offsets => _offsets;

    public ValueTask<short> GetGainAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_gain);

    public async ValueTask SetGainAsync(short value, CancellationToken cancellationToken = default)
    {
        await PutPropertyAsync("gain", "Gain", (int)value);
        _gain = value;
    }

    public short GainMin { get; private set; }
    public short GainMax { get; private set; }
    public IReadOnlyList<string> Gains => _gains;

    public ValueTask<bool> GetFastReadoutAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_fastReadout);

    public async ValueTask SetFastReadoutAsync(bool value, CancellationToken cancellationToken = default)
    {
        await PutBoolPropertyAsync("fastreadout", "FastReadout", value);
        _fastReadout = value;
    }

    // "readoutmode" is a server-side index into the "readoutmodes" list (cached at init); map it to
    // a name on read and back to an index on write, so the mode actually round-trips to the device
    // instead of living in a local field (the previous stub never talked to the server).
    public async ValueTask<string?> GetReadoutModeAsync(CancellationToken cancellationToken = default)
    {
        if (_readoutModes.Length == 0) return null;
        var idx = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "readoutmode", cancellationToken);
        return idx >= 0 && idx < _readoutModes.Length ? _readoutModes[idx] : null;
    }

    public async ValueTask SetReadoutModeAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (value is null) return;
        var idx = Array.IndexOf(_readoutModes, value);
        if (idx < 0) return;
        await PutPropertyAsync("readoutmode", "ReadoutMode", idx);
    }

    // Frame downloaded + decoded once when the server first reports the image ready
    // (see GetImageReadyAsync), then read by the default ICameraDriver.GetImageAsync via the
    // sync ImageData property. No buffer recycling — the float[,] is GC-managed per frame.
    private Imaging.Channel? _imageData;
    private Imaging.ChannelBuffer? _channelBuffer;

    // Recycled frame buffers returned by consumers via ChannelBuffer.onRelease (the DAL pattern);
    // a shape-mismatched buffer (ROI/bin change) is dropped inside DecodeChannel, never re-added.
    private readonly ConcurrentBag<float[,]> _freeBuffers = [];

    public Imaging.Channel? ImageData => _imageData;

    Imaging.ChannelBuffer? ICameraDriver.ChannelBuffer => _channelBuffer;

    public void ReleaseImageData()
    {
        _imageData = null;
        _channelBuffer = null;
    }

    public DateTimeOffset? LastExposureStartTime { get; private set; }

    // Baseline = the requested duration (recorded at StartExposureAsync); refined to the server's
    // actual "lastexposureduration" once the frame is ready (see GetImageReadyAsync). Was null,
    // which zeroed the FITS EXPTIME on every Alpaca frame.
    public TimeSpan? LastExposureDuration => _lastExposureDuration;

    public FrameType LastExposureFrameType { get; internal set; }

    public ValueTask<BitDepth?> GetBitDepthAsync(CancellationToken cancellationToken = default)
    {
        if (_maxADU <= 0) return ValueTask.FromResult<BitDepth?>(null);
        int log2 = (int)MathF.Ceiling(MathF.Log(_maxADU) / MathF.Log(2.0f));
        int bitDepth = ((log2 + 7) / 8) * 8;
        return ValueTask.FromResult(BitDepthEx.FromValue(bitDepth));
    }

    public ValueTask SetBitDepthAsync(BitDepth? value, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Setting bit depth is not supported!");

    // Async-primary members — native async HTTP calls
    public async ValueTask<bool> GetImageReadyAsync(CancellationToken cancellationToken = default)
    {
        var ready = await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "imageready", cancellationToken);

        // Download + decode the frame once, when the server first reports it ready, so the
        // synchronous ImageData property (read by the default GetImageAsync) is populated.
        // Uses the binary ImageBytes transfer; a failed fetch leaves _imageData null so a
        // later poll/retry re-downloads.
        if (ready && _imageData is null)
        {
            var bytes = await Client.GetImageArrayBytesAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "imagearray", cancellationToken);
            // Decode into a recycled buffer when one is available (the DAL recycle loop):
            // the consumer's image.Release() returns the float[,] to _freeBuffers, so a steady
            // capture loop stops allocating a fresh full-frame LOH array per frame.
            var recycled = _freeBuffers.TryTake(out var buffer) ? buffer : null;
            var channel = AlpacaImageBytes.DecodeChannel(bytes, recycled);
            _channelBuffer = new Imaging.ChannelBuffer(channel.Data, onRelease: recycledBuf => _freeBuffers.Add(recycledBuf));
            _imageData = channel;

            // Refine the exposure duration to the server-reported actual (valid once the exposure
            // completes); keep the requested-duration baseline if the read is unsupported/fails.
            try
            {
                var actualSec = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "lastexposureduration", cancellationToken);
                if (actualSec > 0) _lastExposureDuration = TimeSpan.FromSeconds(actualSec);
            }
            catch
            {
                // keep baseline
            }
        }

        return ready;
    }

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

    public ValueTask<double> GetSetCCDTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_setCCDTemperature);

    public async ValueTask SetSetCCDTemperatureAsync(double value, CancellationToken cancellationToken = default)
    {
        await PutDoublePropertyAsync("setccdtemperature", "SetCCDTemperature", value);
        _setCCDTemperature = value;
    }

    public async ValueTask<bool> GetIsPulseGuidingAsync(CancellationToken cancellationToken = default)
        => await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "ispulseguiding", cancellationToken);

    public async ValueTask<DateTimeOffset> StartExposureAsync(TimeSpan duration, FrameType frameType = FrameType.Light, CancellationToken cancellationToken = default)
    {
        // Drop any previous frame so GetImageReadyAsync re-downloads when this one is ready.
        _imageData = null;
        _channelBuffer = null;
        // Baseline the exposure duration to the request; GetImageReadyAsync refines it to the actual.
        _lastExposureDuration = duration;

        await PutMethodAsync("startexposure",
        [
            new("Duration", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)),
            new("Light", frameType.NeedsOpenShutter.ToString())
        ], cancellationToken);

        var startTime = TimeProvider.GetUtcNow();
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
    public int? Aperture { get; set; }
    public int FocusPosition { get; set; } = -1;
    public Filter Filter { get; set; } = Filter.Unknown;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Target? Target { get; set; }
    #endregion
}
