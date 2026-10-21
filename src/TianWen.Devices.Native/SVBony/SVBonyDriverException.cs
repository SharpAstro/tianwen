using System;

namespace TianWen.Lib.Devices.SVBony;

public class SVBonyDriverException(string message) : Exception($"SVBONY: {message}");
