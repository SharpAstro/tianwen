using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;
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

    public static string DeviceIdFromUUID(Guid profileId) => profileId.ToString("D");


    public static Uri CreateProfileUri(Guid profileId, string name, ProfileData data)
        => new UriBuilder(nameof(Profile), nameof(Profile), -1, $"/{DeviceIdFromUUID(profileId)}", $"?{DeviceQueryKey.Data.Key}={EncodeValues(data)}#{name}").Uri;

    public ProfileData? Data
        => _data ??= (Query.QueryValue(DeviceQueryKey.Data) is string encodedValues && JsonSerializer.Deserialize(Base64UrlDecode(encodedValues), ProfileJsonSerializerContextSingleLine.ProfileData) is { } data ? data : null);

    public Profile WithData(ProfileData data)
        => new Profile(ProfileId, DisplayName, data);

    private static readonly ProfileJsonSerializerContext ProfileJsonSerializerContextSingleLine = new ProfileJsonSerializerContext(new JsonSerializerOptions { WriteIndented = false });

    internal static readonly ProfileJsonSerializerContext ProfileJsonSerializerContextIndented = new ProfileJsonSerializerContext(new JsonSerializerOptions { WriteIndented = true });

    static string EncodeValues(ProfileData obj) => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(obj, ProfileJsonSerializerContextSingleLine.ProfileData));

    const string ProfileExt = ".json";

    /// <summary>
    /// Every profile file in <paramref name="profileFolder"/>, listed through <see cref="SharedFile.ListAsync"/>, since
    /// another process may be replacing one of them as it is listed.
    /// </summary>
    /// <param name="expected">The files the previous listing had (null for a first listing): one this listing
    /// lacks is looked for again rather than taken as deleted.</param>
    public static async Task<IReadOnlyList<(Guid profileId, FileInfo file)>> ListExistingProfilesAsync(DirectoryInfo profileFolder,
        IReadOnlyCollection<string>? expected, CancellationToken cancellationToken)
    {
        var profiles = new List<(Guid, FileInfo)>();
        foreach (var file in await SharedFile.ListAsync(profileFolder, ProfileExt, expected, cancellationToken))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out Guid profileId) && profileId != Guid.Empty)
            {
                profiles.Add((profileId, file));
            }
        }
        return profiles;
    }

    public Guid ProfileId => Guid.Parse(DeviceId);

    internal FileInfo ProfileFullPath(IExternal external) => new FileInfo(Path.Combine(external.ProfileFolder.FullName, DeviceIdFromUUID(ProfileId) + ProfileExt));

    public Task SaveAsync(IExternal external, CancellationToken cancellationToken)
        => external.AtomicWriteJsonAsync(
            ProfileFullPath(external).FullName,
            new ProfileDto(ProfileId, DisplayName, Data ?? ProfileData.Empty),
            ProfileJsonSerializerContextIndented.ProfileDto,
            cancellationToken);

    public void Delete(IExternal external)
    {
        var file = ProfileFullPath(external);

        if (file.Exists)
        {
            file.Delete();
        }
    }

    public string Detailed(IDeviceHub deviceUriRegistry)
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
                return NameAndId(device.DisplayName, device.DeviceId);
            }

            // Not discovered (unplugged, a remote rig, a driver not installed here): the URI still
            // carries the name, id, type and class, read by the device's own rules.
            var deviceId = DeviceIdOf(deviceUri);
            if (deviceId.Length == 0)
            {
                return $"{deviceUri} [Unknown Device]";
            }

            var deviceClass = DeviceClassOf(deviceUri);
            var via = deviceClass.Length > 0 ? $" via {deviceClass}" : "";
            return $"{NameAndId(DisplayNameOf(deviceUri), deviceId)} [not discovered: {DeviceTypeOf(deviceUri)}{via}]";
        }

        static string NameAndId(string displayName, string deviceId)
            => displayName is { Length: > 0 } ? $"{displayName} ({deviceId})" : deviceId;
    }
}

[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(ProfileData))]
internal partial class ProfileJsonSerializerContext : JsonSerializerContext
{
}