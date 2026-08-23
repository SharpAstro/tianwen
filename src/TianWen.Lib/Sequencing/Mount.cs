using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Mount(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IMountDriver>(Device, ServiceProvider);
