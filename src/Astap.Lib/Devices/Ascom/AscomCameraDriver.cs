using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Astap.Lib.Devices.Ascom;

public class AscomCameraDriver : AscomDeviceDriverBase, ICameraDriver
{
    public AscomCameraDriver(AscomDevice device) : base(device)
    {
        DeviceConnectedEvent += AscomCameraDriver_DeviceConnectedEvent;
    }

    private void AscomCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected && _comObject is { } obj)
        {
            CanGetCoolerPower = obj.CanGetCoolerPower is bool canGetCoolerPower && canGetCoolerPower;
            CanSetCCDTemperature = obj.CanSetCCDTemperature is bool canSetCCDTemperature && canSetCCDTemperature;
            CanStopExposure =  obj.CanStopExposure is bool canStopExposure && canStopExposure;
            CanAbortExposure =  obj.CanAbortExposure is bool canAbortExposure && canAbortExposure;
            CanFastReadout = obj.CanFastReadout is bool canFastReadout && canFastReadout;
            try
            {
                _ = CoolerOn;
                CanGetCoolerOn = true;
            }
            catch
            {
                CanGetCoolerOn = false;
            }

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
                _ = Offset;
                _ = OffsetMin;
                _ = OffsetMax;
                UsesOffsetValue = true;
            }
            catch
            {
                UsesOffsetValue = false;
            }

            if (!UsesOffsetValue)
            {
                try
                {
                    _ = Offset;
                    _ = obj.Offsets;
                    UsesOffsetMode = true;
                }
                catch
                {
                    UsesOffsetMode = false;
                }
            }

            try
            {
                _ = Gain;
                _ = GainMin;
                _ = GainMax;
                UsesGainValue = true;
            }
            catch
            {
                UsesGainValue = false;
            }

            if (!UsesGainValue)
            {
                try
                {
                    _ = Gain;
                    _ = obj.Gains;
                    UsesGainMode = true;
                }
                catch
                {
                    UsesGainMode = false;
                }
            }
        }
    }

    public bool CanGetCoolerPower { get; private set; }

    public bool CanGetCoolerOn { get; private set; }

    public bool CanGetHeatsinkTemperature { get; private set; }

    public bool CanSetCCDTemperature { get; private set; }

    public bool CanStopExposure { get; private set; }

    public bool CanAbortExposure { get; private set; }

    public bool CanFastReadout { get; private set; }

    public bool UsesGainValue { get; private set; }

    public bool UsesGainMode { get; private set; }

    public bool UsesOffsetValue { get; private set; }

    public bool UsesOffsetMode { get; private set; }

    public double CoolerPower => _comObject?.CoolerPower is double coolerPower ? coolerPower : double.NaN;

    public double HeatSinkTemperature => _comObject?.HeatSinkTemperature is double heatSinkTemperature ? heatSinkTemperature : double.NaN;

    public double CCDTemperature => _comObject?.CCDTemperature is double ccdTemperature ? ccdTemperature : double.NaN;

    public double PixelSizeX => Connected && _comObject?.PixelSizeX is double pixelSizeX ? pixelSizeX : double.NaN;

    public double PixelSizeY => Connected && _comObject?.PixelSizeY is double pixelSizeY ? pixelSizeY : double.NaN;

    public int StartX => Connected && _comObject?.StartX is int startX ? startX : int.MinValue;

    public int StartY => Connected && _comObject?.StartY is int startY ? startY : int.MinValue;

    public int BinX => Connected && _comObject?.BinX is int binX ? binX : 1;

    public int BinY => Connected && _comObject?.BinY is int binY ? binY : 1;

    public int Offset
    {
        get => Connected && _comObject?.InterfaceVersion is 3 && _comObject?.Offset is int offset ? offset : int.MinValue;

        set
        {
            if (Connected && _comObject is { } obj && obj.InterfaceVersion is 3)
            {
                obj.Offset = value;
            }
        }
    }

    public int OffsetMin { get; private set; }

    public int OffsetMax { get; private set; }

    public IEnumerable<string> Offsets => Connected && UsesOffsetMode && _comObject is { } obj ? EnumerateProperty<string>(obj.Offsets) : Enumerable.Empty<string>();

    public short Gain
    {
        get => Connected && _comObject?.Gain is short gain ? gain : short.MinValue;

        set
        {
            if (Connected && _comObject is { } obj)
            {
               obj.Gain = value;
            }
        }
    }

    public short GainMin { get; private set; }

    public short GainMax { get; private set; }

    public IEnumerable<string> Gains => Connected && UsesGainMode && _comObject is { } obj ? EnumerateProperty<string>(obj.Gains) : Enumerable.Empty<string>();

    public bool FastReadout
    {
        get => Connected && CanFastReadout && _comObject?.FastReadout is bool fastReadout && fastReadout;
        set
        {
            if (Connected && CanFastReadout && _comObject is { } obj)
            {
                obj.FastReadout = value;
            }
        }
    }

    private IReadOnlyList<string> ReadoutModes
        => Connected && _comObject is { } obj && EnumerateProperty<string>(obj.ReadoutModes) is IEnumerable<string> modes ? modes.ToList() : Array.Empty<string>();

    public string? ReadoutMode
    {
        get => _comObject?.ReadoutMode is int readoutMode && readoutMode >= 0 && ReadoutModes is { Count: > 0 } modes  && readoutMode < modes.Count ? modes[readoutMode] : null;
        set
        {
            int idx;
            if (Connected && _comObject is { } obj && value is { Length: > 0 } && (idx = ReadoutModes.IndexOf(value)) >= 0)
            {
                obj.ReadoutMode = idx;
            }
        }
    }

    public int[,]? ImageData => _comObject?.ImageArray is int[,] intArray ? intArray : null;

    public bool ImageReady => _comObject?.ImageReady is bool imageReady && imageReady;

    public int MaxADU => _comObject?.MaxADU is int maxADU ? maxADU : int.MinValue;

    public double FullWellCapacity => _comObject?.FullWellCapacity is double fullWellCapacity ? fullWellCapacity : double.NaN;

    public DateTime LastExposureStartTime
        => _comObject?.LastExposureStartTime is string lastExposureStartTime
        && DateTime.TryParse(lastExposureStartTime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : DateTime.MinValue;

    public TimeSpan LastExposureDuration => _comObject?.LastExposureDuration is double lastExposureDuration ? TimeSpan.FromSeconds(lastExposureDuration) : TimeSpan.MinValue;

    public int? BitDepth
    {
        get
        {
            var maxADU = MaxADU;
            if (maxADU is <= 0 || double.IsNaN(FullWellCapacity))
            {
                return null;
            }

            if (maxADU == byte.MaxValue && MaxADU < FullWellCapacity && Name.Contains("QHYCCD", StringComparison.OrdinalIgnoreCase))
            {
                maxADU = (int)FullWellCapacity;
            }

            int log2 = (int)MathF.Ceiling(MathF.Log(maxADU) / MathF.Log(2.0f));
            var bytesPerPixel = (log2 + 7) / 8;
            int bitDepth = bytesPerPixel * 8;

            return bitDepth;
        }
    }

    public void StartExposure(TimeSpan duration, bool light) => _comObject?.StartExposure(duration.TotalSeconds, light);

    public void StopExposure()
    {
        if (CanStopExposure)
        {
            _comObject?.StopExposure();
        }
    }

    public void AbortExposure()
    {
        if (CanAbortExposure)
        {
            _comObject?.AbortExposure();
        }
    }

    public bool CoolerOn
    {
        get => Connected && _comObject?.CoolerOn is bool coolerOn && coolerOn;
        set
        {
            if (Connected && _comObject is { } obj)
            {
                obj.CoolerOn = value;
            }
        }
    }

    public double SetCCDTemperature
    {
        get => Connected && _comObject?.SetCCDTemperature is double setCCDTemperature ? setCCDTemperature : double.NaN;
        set
        {
            if (Connected && CanSetCCDTemperature && _comObject is { } obj)
            {
                obj.SetCCDTemperature = value;
            }
        }
    }

    public CameraState CameraState => Connected && _comObject?.CameraState is int cs ? (CameraState)cs : CameraState.NotConnected;

    public SensorType SensorType => Connected && _comObject?.SensorType is int st ? (SensorType)st : SensorType.Unknown;

    public int BayerOffsetX => _comObject?.BayerOffsetX is int bayerOffsetX ? bayerOffsetX : 0;

    public int BayerOffsetY => _comObject?.BayerOffsetY is int bayerOffsetY ? bayerOffsetY : 0;

    #region Denormalised properties
    public string? Telescope { get; set; }

    public int FocalLength { get; set; } = -1;

    public int FocusPos { get; set; } = -1;

    public Filter Filter { get; set; } = Filter.Unknown;
    #endregion
}