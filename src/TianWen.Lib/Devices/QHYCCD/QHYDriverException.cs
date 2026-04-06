using System;

namespace TianWen.Lib.Devices.QHYCCD;

public class QHYDriverException : Exception
{
    public QHYDriverException(string message) : base($"QHY Error: {message}")
    {
    }

    public QHYDriverException(string message, Exception innerException) : base($"QHY Error: {message}", innerException)
    {
    }
}
