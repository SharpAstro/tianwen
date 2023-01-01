using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Astap.Lib.Base64UrlSafe;

namespace Astap.Lib.Devices.Builtin;

using ValueDictRO = IReadOnlyDictionary<string, Uri>;
using ValueDict = Dictionary<string, Uri>;

public record class Profile(Uri DeviceUri)
    : DeviceBase(DeviceUri)
{
    public Profile(Guid profileId, string name, ValueDictRO values) : this(CreateProfileUri(profileId, name, values))
    {
        _valuesCache = values;
    }

    public static Uri CreateProfileUri(Guid profileId, string name, ValueDictRO? values = null)
        => new UriBuilder(UriScheme, nameof(Profile), -1, $"/{profileId:D}", $"?displayName={name}&values={EncodeValues(values ?? new ValueDict())}#{nameof(Profile)}").Uri;

    private ValueDictRO? _valuesCache;
    public ValueDictRO Values
        => _valuesCache ??= (
            Query["values"] is string encodedValues && JsonSerializer.Deserialize<ValueDict>(Base64UrlDecode(encodedValues)) is { } dict
                ? dict
                : new ValueDict()
        );

    static string EncodeValues(ValueDictRO values)
        => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { WriteIndented = false }));

    const string ProfileExt = ".json";

    public static IEnumerable<(Guid profileId, FileInfo file)> ListExistingProfiles(DirectoryInfo profileFolder)
    {
        foreach (var file in profileFolder.EnumerateFiles("*" + ProfileExt))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out Guid profileId))
            {
                yield return (profileId, file);
            }
        }
    }

    public static async Task<IList<Profile>> LoadExistingProfilesAsync(DirectoryInfo profileFolder)
    {
        var profiles = new List<Profile>();
        foreach (var (_, file) in ListExistingProfiles(profileFolder))
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (await JsonSerializer.DeserializeAsync<ProfileDto>(stream) is { } profileDto
                    && !string.IsNullOrWhiteSpace(profileDto.Name)
                )
                {
                    profiles.Add(new Profile(profileDto.ProfileId, profileDto.Name, profileDto.Values));
                }
            }
            catch (Exception ex) when (Debugger.IsAttached)
            {
                GC.KeepAlive(ex);
            }
        }

        return profiles;
    }

    public Guid ProfileId => Guid.Parse(DeviceId);

    public Task SaveAsync(DirectoryInfo profileFolder)
    {
        var (_, file) = ListExistingProfiles(profileFolder).FirstOrDefault(x => x.profileId == ProfileId);

        FileMode mode;
        if (file is null)
        {
            file = new FileInfo(Path.Combine(profileFolder.FullName, ProfileId.ToString("D") + ProfileExt));
            mode = FileMode.CreateNew;
        }
        else
        {
            mode = FileMode.Truncate;
        }

        using var stream = file.Open(mode, FileAccess.Write, FileShare.None);
        return JsonSerializer.SerializeAsync(stream, new ProfileDto(ProfileId, DisplayName, Values), new JsonSerializerOptions { WriteIndented = true });
    }

    protected override object? NewImplementationFromDevice() => null;

    record ProfileDto(Guid ProfileId, string Name, ValueDictRO Values);
}