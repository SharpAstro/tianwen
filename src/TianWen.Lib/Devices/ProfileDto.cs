using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Devices;

internal record ProfileDto(Guid ProfileId, string Name, ProfileData Data);

public readonly record struct ProfileData(Uri Mount, Uri Guider, ImmutableArray<OTAData> OTAs, Uri? GuiderFocuser = null, int? OAG_OTA_Index = null)
{
    public static readonly ProfileData Empty = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri, []);
}

public readonly record struct OTAData(string Name, int FocalLength, Uri Camera, Uri? Cover, Uri? Focuser, Uri? FilterWheel, bool? PreferOutwardFocus, bool? OutwardIsPositive);