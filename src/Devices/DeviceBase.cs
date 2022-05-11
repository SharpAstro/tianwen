using System;

namespace Astap.Lib.Devices;

public abstract record class DeviceBase(string DeviceId, string DeviceType, string DisplayName)
{
    public Uri DeviceUri => new($"{GetType().Name}:{DeviceType}/{DisplayName}");
}