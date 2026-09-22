using System;
using static PlayerOne.SDK.PlayerOneCamera;

namespace TianWen.Lib.Devices.PlayerOne;

public class PlayerOneDriverException : Exception
{
    public PlayerOneDriverException(POAErrors errorCode, string message)
        : base($"POA Error {errorCode}: {message}")
    {
        Data["Error Code"] = errorCode;
    }
}
