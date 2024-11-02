using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Devices.Fake;

internal class FakeGuider(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IGuider
{
    public override DeviceType DriverType => DeviceType.DedicatedGuiderSoftware;

    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;

    public (int width, int height)? CameraFrameSize() => new(640, 480);

    public void ConnectEquipment()
    {
        throw new NotImplementedException();
    }

    public void DisconnectEquipment()
    {
        throw new NotImplementedException();
    }

    public void Dither(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false)
    {
        throw new NotImplementedException();
    }

    public TimeSpan ExposureTime()
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<string> GetEquipmentProfiles() => ["Fake Profile"];

    public GuideStats? GetStats()
    {
        throw new NotImplementedException();
    }

    public void GetStatus(out string? appState, out double avgDist)
    {
        throw new NotImplementedException();
    }

    public void Guide(double settlePixels, double settleTime, double settleTimeout)
    {
        throw new NotImplementedException();
    }

    public bool IsGuiding()
    {
        throw new NotImplementedException();
    }

    public bool IsLooping()
    {
        throw new NotImplementedException();
    }

    public bool IsSettling()
    {
        throw new NotImplementedException();
    }

    public bool Loop(TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        throw new NotImplementedException();
    }

    public void Pause()
    {
        throw new NotImplementedException();
    }

    public double PixelScale()
    {
        throw new NotImplementedException();
    }

    public string? SaveImage(string outputFolder)
    {
        throw new NotImplementedException();
    }

    public void StopCapture(TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        throw new NotImplementedException();
    }

    public bool TryGetActiveProfileName([NotNullWhen(true)] out string? activeProfileName)
    {
        throw new NotImplementedException();
    }

    public bool TryGetSettleProgress([NotNullWhen(true)] out SettleProgress? settleProgress)
    {
        throw new NotImplementedException();
    }

    public void Unpause()
    {
        throw new NotImplementedException();
    }
}
