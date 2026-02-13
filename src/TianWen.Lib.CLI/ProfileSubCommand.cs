using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class ProfileSubCommand(IConsoleHost consoleHost, Option<string?> selectedProfileOption)
{
    private readonly Argument<string> profileNameOrIdArg = new Argument<string>("profileNameOrId") { Description = "Name or ID of the profile" };
    private readonly Argument<string> profileNameArg = new Argument<string>("profileName") { Description = "Name of the new profile" };
    private readonly Argument<string> deviceIdArg = new Argument<string>("deviceId") { Description = "Device ID" };

    public Command Build()
    {
        var listProfilesCommand = new Command("list", "List all profiles");
        listProfilesCommand.SetAction(ListProfilesActionAsync);

        var deleteProfileCommand = new Command("delete", "Delete a profile")
        {
            Arguments = { profileNameOrIdArg }
        };
        deleteProfileCommand.SetAction(DeleteProfileActionAsync);

        var createProfileCommand = new Command("create", "Create a new empty profile")
        {
            Arguments = { profileNameArg }
        };
        createProfileCommand.SetAction(CreateProfileActionAsync);

        var addDeviceCommand = new Command("add", "Add a device to a profile")
        {
            Arguments = { deviceIdArg }
        };
        addDeviceCommand.SetAction(AddDeviceActionAsync);

        return new Command("profile", "Manage profiles")
        {
            Subcommands = {
                listProfilesCommand,
                deleteProfileCommand,
                createProfileCommand,
                addDeviceCommand
            }
        };
    }

    internal async Task CreateProfileActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var profileName = parseResult.GetRequiredValue(profileNameArg);

        var newProfile = new Profile(Guid.NewGuid(), profileName, ProfileData.Empty);
        await newProfile.SaveAsync(consoleHost.External, cancellationToken);

        consoleHost.WriteScrollable($"Created new profile '{newProfile.DisplayName}' with ID {newProfile.ProfileId}");
    }

    internal async Task ListProfilesActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var allProfiles = await ListProfilesAsync(cancellationToken);

        var selectedProfile = parseResult.GetSelected(allProfiles, selectedProfileOption);

        foreach (var profile in allProfiles)
        {
            var isSelected = profile.ProfileId == selectedProfile?.ProfileId;

            var selectedChar = isSelected ? ">" : " ";

            consoleHost.WriteScrollable($"\n{selectedChar} {profile.Detailed(consoleHost.DeviceUriRegistry)}");
        }
    }

    internal async Task AddDeviceActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var allProfiles = await ListProfilesAsync(cancellationToken);

        var devices = await consoleHost.ListAllDevicesAsync(DeviceDiscoveryOption.None, cancellationToken);

        var selectedProfile = parseResult.GetSelected(allProfiles, selectedProfileOption);
        var deviceId = parseResult.GetRequiredValue(deviceIdArg);

        if (selectedProfile is { })
        {
            var matchingDevices = devices.Where(d => d.DeviceId == deviceId).ToList();

            if (matchingDevices.Count is 1)
            {
                var device = matchingDevices[0];
                var uri = device.DeviceUri;

                var data = selectedProfile.Data ?? new ProfileData();

                var newData = device.DeviceType switch
                {
                    DeviceType.Mount => data with { Mount = uri },
                    DeviceType.Guider => data with { Guider = uri },
                    DeviceType.Camera when data.OTAs.Length is 1 => data with { OTAs = [data.OTAs[0] with { Camera = uri }] },
                    DeviceType.CoverCalibrator when data.OTAs.Length is 1 => data with { OTAs = [data.OTAs[0] with { Cover = uri }] },
                    DeviceType.Focuser when data.OTAs.Length is 1 => data with { OTAs = [data.OTAs[0] with { Focuser = uri }] },
                    DeviceType.FilterWheel when data.OTAs.Length is 1 => data with { OTAs = [data.OTAs[0] with { FilterWheel = uri }] },
                    _ => data
                };

                var updatedProfile = selectedProfile.WithData(newData);
                await updatedProfile.SaveAsync(consoleHost.External, cancellationToken);

                await ListProfilesActionAsync(parseResult, cancellationToken);
            }
            else if (matchingDevices.Count is 0)
            {
                consoleHost.WriteError($"No device found with ID '{deviceId}'");
            }
            else
            {
                consoleHost.WriteError($"Multiple devices found with ID '{deviceId}':");
                foreach (var device in matchingDevices)
                {
                    consoleHost.WriteError($"- {device}");
                }
            }
        }
    }

    internal async Task DeleteProfileActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var profileNameOrId = parseResult.GetRequiredValue(profileNameOrIdArg);

        var profiles = await ListProfilesAsync(cancellationToken);
        Profile profileToDelete;
        if (Guid.TryParse(profileNameOrId, out var profileId))
        {
            if (profiles.FirstOrDefault(p => p.ProfileId == profileId) is { } profile)
            {
                profileToDelete = profile;
            }
            else
            {
                consoleHost.WriteError($"No profile found with ID '{profileId}'");
                return;
            }
        }
        else
        {
            var matchingProfiles = profiles.Where(p => string.Equals(p.DisplayName, profileNameOrId, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matchingProfiles.Count is 1)
            {
                profileToDelete = matchingProfiles[0];
            }
            else if (matchingProfiles.Count > 1)
            {
                consoleHost.WriteError($"Multiple profiles found with name '{profileNameOrId}':");
                foreach (var profile in matchingProfiles)
                {
                    consoleHost.WriteError($"- {profile.ProfileId}");
                }
                return;
            }
            else
            {
                consoleHost.WriteError($"No profiles found with name '{profileNameOrId}'");
                return;
            }
        }

        profileToDelete.Delete(consoleHost.External);

        consoleHost.WriteScrollable($"Deleted profile '{profileToDelete.DisplayName}' ({profileToDelete.ProfileId})");

        // refresh cache
        var profilesAfterDelete = await ListProfilesAsync(cancellationToken);
    }

    private Task<IReadOnlyCollection<Profile>> ListProfilesAsync(CancellationToken cancellationToken) =>
        consoleHost.ListDevicesAsync<Profile>(DeviceType.Profile, DeviceDiscoveryOption.Force, cancellationToken);
}
