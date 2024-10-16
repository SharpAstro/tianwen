﻿using System;

namespace Astap.Lib.Devices.ZWO;

public record class ZWODevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public ZWODevice(DeviceType deviceType, string deviceId, string displayName)
        : this(new Uri($"{deviceType}://{typeof(ZWODevice).Name}/{deviceId}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override object? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Camera => new ZWOCameraDriver(this, external),
        DeviceType.FilterWheel => new ZWOFilterWheelDriver(this, external),
        DeviceType.Focuser => new ZWOFocuserDriver(this, external),
        _ => null
    };
}