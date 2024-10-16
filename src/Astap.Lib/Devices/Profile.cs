using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Astap.Lib.Base64UrlSafe;

namespace Astap.Lib.Devices;

using ValueDict = Dictionary<string, Uri>;
using ValueDictRO = IReadOnlyDictionary<string, Uri>;

/// <summary>
/// Build-in profile device, see <see cref="DeviceType.Profile"/>.
/// </summary>
/// <param name="DeviceUri">profile descriptor</param>
public record class Profile(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public static readonly Profile Root = new Profile(CreateProfileUri(Guid.Empty, "Root"));

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
    {
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(values, ValueSerializerOptions));
    }

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

    public async Task SaveAsync(DirectoryInfo profileFolder)
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
        await JsonSerializer.SerializeAsync(stream, new ProfileDto(ProfileId, DisplayName, Values), ProfileSerializerOptions);
    }

    protected override object? NewInstanceFromDevice(IExternal external) => new ProfileIterator(external);
}

record ProfileDto(Guid ProfileId, string Name, ValueDictRO Values);

record ProfileIterator(IExternal External) : IDeviceSource<Profile>
{
    public bool IsSupported => true;

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Profile];

    public IEnumerable<Profile> RegisteredDevices(DeviceType deviceType)
    {
        foreach (var (profileId, file) in Profile.ListExistingProfiles(External.ProfileFolder))
        {
            Profile? profile;
            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                if (JsonSerializer.Deserialize<ProfileDto>(stream) is { } profileDto && !string.IsNullOrWhiteSpace(profileDto.Name))
                {
                    profile = new Profile(profileDto.ProfileId, profileDto.Name, profileDto.Values);
                }
                else
                {
                    External.LogWarning($"Skipping invalid profile {profileId} in file {file}");
                    profile = null;
                }
            }
            catch (Exception ex)
            {
                External.LogException(ex, $"Failed to load profile {profileId} in file {file}");
                profile = null;
            }

            if (profile is not null)
            {
                yield return profile;
            }
        }
    }
}