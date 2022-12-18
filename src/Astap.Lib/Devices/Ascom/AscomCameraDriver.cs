﻿using System;

namespace Astap.Lib.Devices.Ascom;

public class AscomCameraDriver : AscomDeviceDriverBase, ICameraDriver
{
    public AscomCameraDriver(AscomDevice device) : base(device)
    {
        this.DeviceConnectedEvent += AscomCameraDriver_DeviceConnectedEvent;
    }

    private void AscomCameraDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected && _comObject is var obj and not null)
        {
            CanGetCoolerPower = obj.CanGetCoolerPower is bool canGetCoolerPower && canGetCoolerPower;
            CanSetCCDTemperature = obj.CanSetCCDTemperature is bool canSetCCDTemperature && canSetCCDTemperature;
            CanStopExposure =  obj.CanStopExposure is bool canStopExposure && canStopExposure;
            CanAbortExposure =  obj.CanAbortExposure is bool canAbortExposure && canAbortExposure;
        }
    }

    public bool CanGetCoolerPower { get; private set; }

    public bool CanSetCCDTemperature { get; private set; }

    public bool CanStopExposure { get; private set; }

    public bool CanAbortExposure { get; private set; }

    public double CoolerPower => _comObject?.CoolerPower is double coolerPower ? coolerPower : double.NaN;

    public double HeatSinkTemperature => _comObject?.HeatSinkTemperature is double heatSinkTemperature ? heatSinkTemperature : double.NaN;

    public double CCDTemperature => _comObject?.CCDTemperature is double ccdTemperature ? ccdTemperature : double.NaN;

    public double PixelSizeX => Connected && _comObject?.PixelSizeX is double pixelSizeX ? pixelSizeX : double.NaN;

    public double PixelSizeY => Connected && _comObject?.PixelSizeY is double pixelSizeY ? pixelSizeY : double.NaN;

    public int StartX => Connected && _comObject?.StartX is int startX ? startX : int.MinValue;

    public int StartY => Connected && _comObject?.StartY is int startY ? startY : int.MinValue;

    public int[,]? ImageData => _comObject?.ImageArray is int[,] intArray ? intArray : null;

    public bool ImageReady => _comObject?.ImageReady is bool imageReady && imageReady;

    public int MaxADU => _comObject?.MaxADU is int maxADU ? maxADU : int.MinValue;

    public double FullWellCapacity => _comObject?.FullWellCapacity is double fullWellCapacity ? fullWellCapacity : double.NaN;

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
            if (Connected && _comObject is var obj and not null)
            {
                obj.CoolerOn = value;
            }
        }
    }
}