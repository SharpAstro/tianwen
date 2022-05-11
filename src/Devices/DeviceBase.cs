using System;
using System.Web;

namespace Astap.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    public string DeviceType => HttpUtility.HtmlDecode(DeviceUri.Fragment.TrimStart('#'));

    public string DeviceId => string.Join('/', DeviceUri.Segments[1..]);

    public string DisplayName => HttpUtility.ParseQueryString(DeviceUri.Query)["displayName"] ?? "";

    public string DeviceClass => DeviceUri.Host;
}