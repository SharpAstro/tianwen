using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record Camera(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<ICameraDriver>(Device, ServiceProvider);
