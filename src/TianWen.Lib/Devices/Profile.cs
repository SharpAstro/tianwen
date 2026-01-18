using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Base64UrlSafe;

namespace TianWen.Lib.Devices;

/// <summary>
/// Build-in profile device, see <see cref="DeviceType.Profile"/>.
/// </summary>
/// <param name="DeviceUri">profile descriptor</param>
public record class Profile(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    private ProfileData? _data;

    public Profile(Guid profileId, string name, ProfileData data) : this(CreateProfileUri(profileId, name, data))
    {
        _data = data;
    }

    const string DataKey = "data";

    public static string DeviceIdFromUUID(Guid profileId) => profileId.ToString("D");


    public static Uri CreateProfileUri(Guid profileId, string name, ProfileData data)
        => new UriBuilder(nameof(Profile), nameof(Profile), -1, $"/{DeviceIdFromUUID(profileId)}", $"?{DataKey}={EncodeValues(data)}#{name}").Uri;

    public ProfileData? Data
        => _data ??= (Query[DataKey] is string encodedValues && JsonSerializer.Deserialize(Base64UrlDecode(encodedValues), ProfileJsonSerializerContextSingleLine.ProfileData) is { } data ? data : null);

    public Profile WithData(ProfileData data)
        => new Profile(ProfileId, DisplayName, data);

    private static readonly ProfileJsonSerializerContext ProfileJsonSerializerContextSingleLine = new ProfileJsonSerializerContext(new JsonSerializerOptions { WriteIndented = false });

    internal static readonly ProfileJsonSerializerContext ProfileJsonSerializerContextIndented = new ProfileJsonSerializerContext(new JsonSerializerOptions { WriteIndented = true });

    static string EncodeValues(ProfileData obj) => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(obj, ProfileJsonSerializerContextSingleLine.ProfileData));

    const string ProfileExt = ".json";

    public static IEnumerable<(Guid profileId, FileInfo file)> ListExistingProfiles(DirectoryInfo profileFolder)
    {
        foreach (var file in profileFolder.EnumerateFiles("*" + ProfileExt))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out Guid profileId) && profileId != Guid.Empty)
            {
                yield return (profileId, file);
            }
        }
    }

    public Guid ProfileId => Guid.Parse(DeviceId);

    internal FileInfo ProfileFullPath(IExternal external) => new FileInfo(Path.Combine(external.ProfileFolder.FullName, DeviceIdFromUUID(ProfileId) + ProfileExt));

    public async Task SaveAsync(IExternal external, CancellationToken cancellationToken)
    {
        var file = ProfileFullPath(external);

        using var stream = file.Open(file.Exists ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new ProfileDto(ProfileId, DisplayName, Data ?? ProfileData.Empty), ProfileJsonSerializerContextIndented.ProfileDto, cancellationToken);
    }

    public void Delete(IExternal external)
    {
        var file = ProfileFullPath(external);

        if (file.Exists)
        {
            file.Delete();
        }
    }

    public string Detailed(IDeviceUriRegistry deviceUriRegistry)
    {
        var sb = new StringBuilder()
            .Append($"Profile: {DisplayName} ({ProfileId})");

        if (Data is { } data)
        {
            var none = NoneDevice.Instance.DisplayName;
            var otaCount = data.OTAs.Length;

            sb.Append($"\n  Mount: {(data.Mount is { } mount ? DeviceInfo(mount) : none)}");
            sb.Append($"\n  Guider: {(data.Guider is { } guider ? DeviceInfo(guider) : none)}");

            if (otaCount == 0)
            {
                sb.Append("\n  <No Telescopes>");
            }
            else
            {
                for (var i = 0; i < otaCount; i++)
                {
                    var ota = data.OTAs[i];
                    sb.AppendFormat($"\n  Telescope #{i + 1}:\n");
                    sb.AppendFormat($"    Name: {ota.Name}\n");
                    sb.AppendFormat($"    Focal Length: {ota.FocalLength} mm\n");
                    sb.AppendFormat($"    Camera: {ota.Camera}\n");
                    sb.AppendFormat($"    Cover: {(ota.Cover is { } cover ? DeviceInfo(cover) : none)}\n");
                    sb.AppendFormat($"    Focuser: {(ota.Focuser is { } focuser ? DeviceInfo(focuser) : none)}\n");
                    sb.AppendFormat($"    Filter Wheel: {(ota.FilterWheel is { } filterWheel ? DeviceInfo(filterWheel) : none)}\n");
                    sb.AppendFormat($"    Prefer Outward Focus: {(ota.PreferOutwardFocus.HasValue ? ota.PreferOutwardFocus.Value.ToString() : "<Default>")}\n");
                    sb.AppendFormat($"    Outward Is Positive: {(ota.OutwardIsPositive.HasValue ? ota.OutwardIsPositive.Value.ToString() : "<Default>")}\n");
                }
            }
        }
        else
        {
            sb.AppendLine("\n  <No Data>");
        }

        return sb.ToString();

        string DeviceInfo(Uri deviceUri)
        {
            if (deviceUriRegistry.TryGetDeviceFromUri(deviceUri, out var device))
            {
                return device.DisplayName is { Length: > 0 } ? $"{device.DisplayName} ({device.DeviceId})" : device.DeviceId;
            }
            else
            {
                // TODO try to parse URI manually
                return $"{deviceUri} [Unknown Device]";
            }
        }
    }
}

[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(ProfileData))]
internal partial class ProfileJsonSerializerContext : JsonSerializerContext
{
}