using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static Astap.Lib.Base64UrlSafe;

namespace Astap.Lib.Devices.Builtin;

using ValueDict = IReadOnlyDictionary<string, Uri>;

public record class Profile(Uri DeviceUri)
    : DeviceBase(DeviceUri)
{
    public Profile(Guid profileId, string name, ValueDict values)
        : this(new Uri($"{UriScheme}://{nameof(Profile)}/{profileId:D}?displayName={name}&values={EncodeValues(values)}#{nameof(Profile)}"))
    {

    }

    public ValueDict Values
        => Query["values"] is string encodedValues && JsonSerializer.Deserialize<ValueDict>(Base64UrlDecode(encodedValues)) is { } dict
            ? dict
            : new Dictionary<string, Uri>();

    static string EncodeValues(ValueDict values) => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { WriteIndented = false }));

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

    public static async Task<IList<Profile>> LoadExistingProfilesAsync(DirectoryInfo profileFolder, string profileKey)
    {
        var profiles = new List<Profile>();
        foreach (var (_, file) in ListExistingProfiles(profileFolder))
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (await JsonSerializer.DeserializeAsync<ValueDict>(stream) is ValueDict values
                    && values[profileKey] is Uri profileUri
                    && TryFromUri(profileUri, out var profileInfo)
                    && profileInfo.DeviceType == nameof(Profile)
                    && Guid.TryParse(profileInfo.DeviceId, out var profileId)
                )
                {
                    var profile = new Profile(profileId, profileInfo.DisplayName, values);
                    profiles.Add(profile);
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
        return JsonSerializer.SerializeAsync(stream, Values, new JsonSerializerOptions { WriteIndented = true });
    }

    protected override object? NewImplementationFromDevice() => null;
}