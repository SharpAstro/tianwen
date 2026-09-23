using System;

namespace TianWen.Lib.Devices.ToupTek;

public class ToupTekDriverException(string message) : Exception($"ToupTek: {message}");
