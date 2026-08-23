using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

public record Weather(DeviceBase Device, IServiceProvider ServiceProvider) : ControllableDeviceBase<IWeatherDriver>(Device, ServiceProvider);
