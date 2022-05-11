using System;

namespace Astap.Lib.Devices;

public record class NoneDevice() : DeviceBase(new Uri($"device://{typeof(NoneDevice).Name}/None#None"));