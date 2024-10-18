using System;
using static ZWOptical.SDK.EAFFocuser1_6;
using static ZWOptical.SDK.ASICamera2;
using static ZWOptical.SDK.EFW1_7;

namespace TianWen.Lib.Devices.ZWO;

public class ZWODriverException : Exception
{
    public ZWODriverException(ASI_ERROR_CODE errorCode, string message)
        : base($"ASI Error {errorCode}: {message}")
    {
        Data["Error Code"] = errorCode;
    }

    public ZWODriverException(EAF_ERROR_CODE errorCode, string message)
        : base($"EAF Error {errorCode}: {message}")
    {
        Data["Error Code"] = errorCode;
    }

    public ZWODriverException(EFW_ERROR_CODE errorCode, string message)
        : base($"EFW Error {errorCode}: {message}")
    {
        Data["Error Code"] = errorCode;
    }
}
