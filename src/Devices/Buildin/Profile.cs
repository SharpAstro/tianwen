using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Astap.Lib.Devices.Buildin;

using ValueDict = Dictionary<string, Uri>;

public record class Profile(Uri DeviceUri)
    : DeviceBase(DeviceUri)
{
    public Profile(Guid profileId, string name, ValueDict values)
        : this(new Uri($"{UriScheme}://{nameof(Profile)}/{profileId:D}?displayName={name}#{nameof(Profile)}"))
    {
        Values = values;
    }

    const string ProfileExt = ".json";

    static readonly string ProfileFolderName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Name ?? nameof(Profile),
        nameof(Profile) + "s"
    );

    static DirectoryInfo ProfileFolder => Directory.CreateDirectory(ProfileFolderName);

    public ValueDict? Values { get; init; }

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
            file = new FileInfo(Path.Combine(ProfileFolderName, ProfileId.ToString("D") + ProfileExt));
            mode = FileMode.CreateNew;
        }
        else
        {
            mode = FileMode.Truncate;
        }

        using var stream = file.Open(mode, FileAccess.Write, FileShare.None);
        return JsonSerializer.SerializeAsync(stream, Values, new JsonSerializerOptions { WriteIndented = true });
    }
}