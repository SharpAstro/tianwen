using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Switch(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<ISwitchDriver>(Device, ServiceProvider);
