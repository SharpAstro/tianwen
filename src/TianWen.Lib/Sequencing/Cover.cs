using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Cover(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<ICoverDriver>(Device, ServiceProvider);
