using TianWen.Lib.Imaging;
using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Fake;

internal sealed class FakeCameraDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), ICameraDriver
{
    public bool CanGetCoolerPower => throw new NotImplementedException();

    public bool CanGetCoolerOn => throw new NotImplementedException();

    public bool CanSetCoolerOn => throw new NotImplementedException();

    public bool CanGetCCDTemperature => throw new NotImplementedException();

    public bool CanSetCCDTemperature => throw new NotImplementedException();

    public bool CanGetHeatsinkTemperature => throw new NotImplementedException();

    public bool CanStopExposure => throw new NotImplementedException();

    public bool CanAbortExposure => throw new NotImplementedException();

    public bool CanFastReadout => throw new NotImplementedException();

    public bool CanSetBitDepth => throw new NotImplementedException();

    public bool UsesGainValue => throw new NotImplementedException();

    public bool UsesGainMode => throw new NotImplementedException();

    public bool UsesOffsetValue => throw new NotImplementedException();

    public bool UsesOffsetMode => throw new NotImplementedException();

    public double PixelSizeX => throw new NotImplementedException();

    public double PixelSizeY => throw new NotImplementedException();

    public short MaxBinX => throw new NotImplementedException();

    public short MaxBinY => throw new NotImplementedException();

    public int BinX { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int BinY { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int StartX { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int StartY { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public int CameraXSize => throw new NotImplementedException();

    public int CameraYSize => throw new NotImplementedException();

    public string? ReadoutMode { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public bool FastReadout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public Float32HxWImageData? ImageData => throw new NotImplementedException();

    public bool ImageReady => throw new NotImplementedException();

    public bool CoolerOn { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double CoolerPower => throw new NotImplementedException();

    public double SetCCDTemperature { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public double HeatSinkTemperature => throw new NotImplementedException();

    public double CCDTemperature => throw new NotImplementedException();

    public BitDepth? BitDepth { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public short Gain { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public short GainMin => throw new NotImplementedException();

    public short GainMax => throw new NotImplementedException();

    public IEnumerable<string> Gains => throw new NotImplementedException();

    public int Offset { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public int OffsetMin => throw new NotImplementedException();

    public int OffsetMax => throw new NotImplementedException();

    public IEnumerable<string> Offsets => throw new NotImplementedException();

    public double ExposureResolution => throw new NotImplementedException();

    public int MaxADU => throw new NotImplementedException();

    public double FullWellCapacity => throw new NotImplementedException();

    public double ElectronsPerADU => throw new NotImplementedException();

    public DateTimeOffset? LastExposureStartTime => throw new NotImplementedException();

    public TimeSpan? LastExposureDuration => throw new NotImplementedException();

    public FrameType LastExposureFrameType => throw new NotImplementedException();

    public SensorType SensorType => throw new NotImplementedException();

    public int BayerOffsetX => throw new NotImplementedException();

    public int BayerOffsetY => throw new NotImplementedException();

    public CameraState CameraState => throw new NotImplementedException();

    public string? Telescope { get; set; }
    public int FocalLength { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Filter Filter { get; set; } = Filter.Unknown;
    public int FocusPosition { get; set; }
    public Target? Target { get; set; }

    public override DeviceType DriverType => DeviceType.Camera;

    public void AbortExposure()
    {
        throw new NotImplementedException();
    }

    public DateTimeOffset StartExposure(TimeSpan duration, FrameType frameType = FrameType.Light)
    {
        throw new NotImplementedException();
    }

    public void StopExposure()
    {
        throw new NotImplementedException();
    }
}