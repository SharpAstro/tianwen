using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task SaveAsync(IExternal external)
    {
        var file = new FileInfo(Path.Combine(external.ProfileFolder.FullName, DeviceIdFromUUID(ProfileId) + ProfileExt));

        using var stream = file.Open(file.Exists ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new ProfileDto(ProfileId, DisplayName, Data ?? ProfileData.Empty), ProfileJsonSerializerContextIndented.ProfileDto);
    }
}

[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(ProfileData))]
internal partial class ProfileJsonSerializerContext : JsonSerializerContext
{
}