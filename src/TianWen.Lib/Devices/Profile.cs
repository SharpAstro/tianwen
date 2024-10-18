using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using static TianWen.Lib.Base64UrlSafe;

namespace TianWen.Lib.Devices;

using ValueDict = Dictionary<string, Uri>;
using ValueDictRO = IReadOnlyDictionary<string, Uri>;

/// <summary>
/// Build-in profile device, see <see cref="DeviceType.Profile"/>.
/// </summary>
/// <param name="DeviceUri">profile descriptor</param>
public record class Profile(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public Profile(Guid profileId, string name, ValueDictRO values) : this(CreateProfileUri(profileId, name, values))
    {
        _valuesCache = values;
    }

    public static Uri CreateProfileUri(Guid profileId, string name, ValueDictRO? values = null)
        => new UriBuilder(nameof(Profile), nameof(Profile), -1, $"/{profileId:D}", $"?values={EncodeValues(values ?? new ValueDict())}#{name}").Uri;

    private ValueDictRO? _valuesCache;
    public ValueDictRO Values
        => _valuesCache ??= (
            Query["values"] is string encodedValues && JsonSerializer.Deserialize<ValueDict>(Base64UrlDecode(encodedValues)) is { } dict
                ? dict
                : new ValueDict()
        );

    private static readonly JsonSerializerOptions ValueSerializerOptions = new JsonSerializerOptions { WriteIndented = false };

    private static readonly JsonSerializerOptions ProfileSerializerOptions = new JsonSerializerOptions { WriteIndented = true };

    static string EncodeValues(ValueDictRO values)
        => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(values, ValueSerializerOptions));

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
        var file = new FileInfo(Path.Combine(external.ProfileFolder.FullName, ProfileId.ToString("D") + ProfileExt));

        using var stream = file.Open(file.Exists ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, new ProfileDto(ProfileId, DisplayName, Values), ProfileSerializerOptions);
    }
}