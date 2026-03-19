using System.CommandLine;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.CLI;

internal class ProfileSubCommand(IConsoleHost consoleHost, Option<string?> selectedProfileOption)
{
    private readonly Argument<string> profileNameOrIdArg = new("profileNameOrId") { Description = "Name or ID of the profile" };
    private readonly Argument<string> profileNameArg = new("profileName") { Description = "Name of the new profile" };
    private readonly Argument<string> deviceIdArg = new("deviceId") { Description = "Device ID" };
    private readonly Argument<string> otaNameArg = new("name") { Description = "Display name for the OTA (e.g. 'RC8', 'Samyang 135')" };
    private readonly Argument<int> otaIndexArg = new("index") { Description = "OTA index (0-based)" };

    // Options for add-ota
    private readonly Option<int> focalLengthOption = new("--focal-length") { Description = "Focal length in mm", Required = true };
    private readonly Option<string> cameraOption = new("--camera") { Description = "Camera device ID", Required = true };
    private readonly Option<string?> focuserOption = new("--focuser") { Description = "Focuser device ID" };
    private readonly Option<string?> filterWheelOption = new("--filter-wheel") { Description = "Filter wheel device ID" };
    private readonly Option<string?> coverOption = new("--cover") { Description = "Cover/calibrator device ID" };
    private readonly Option<int?> apertureOption = new("--aperture") { Description = "Aperture in mm" };
    private readonly Option<OpticalDesign> opticalDesignOption = new("--optical-design") { Description = "Optical design type", DefaultValueFactory = _ => OpticalDesign.Unknown };

    // Options for set-site
    private readonly Option<double> siteLatOption = new("--lat") { Description = "Site latitude in degrees (-90..+90, positive=north)", Required = true };
    private readonly Option<double> siteLonOption = new("--lon") { Description = "Site longitude in degrees (-180..+180, positive=east)", Required = true };
    private readonly Option<double?> siteElevOption = new("--elevation") { Description = "Site elevation in metres above sea level" };

    // Option for add (per-OTA device targeting)
    private readonly Option<int> otaOption = new("--ota") { Description = "OTA index to target (0-based)", DefaultValueFactory = _ => 0 };

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
            Arguments = { deviceIdArg },
            Options = { otaOption }
        };
        addDeviceCommand.SetAction(AddDeviceActionAsync);

        var setMountCommand = new Command("set-mount", "Set the mount device on a profile")
        {
            Arguments = { deviceIdArg }
        };
        setMountCommand.SetAction(SetMountActionAsync);

        var setGuiderCommand = new Command("set-guider", "Set the guider device on a profile")
        {
            Arguments = { deviceIdArg }
        };
        setGuiderCommand.SetAction(SetGuiderActionAsync);

        var addOtaCommand = new Command("add-ota", "Add an OTA (optical tube assembly) to a profile")
        {
            Arguments = { otaNameArg },
            Options = { focalLengthOption, cameraOption, focuserOption, filterWheelOption, coverOption, apertureOption, opticalDesignOption }
        };
        addOtaCommand.SetAction(AddOtaActionAsync);

        var removeOtaCommand = new Command("remove-ota", "Remove an OTA from a profile by index")
        {
            Arguments = { otaIndexArg }
        };
        removeOtaCommand.SetAction(RemoveOtaActionAsync);

        var setSiteCommand = new Command("set-site", "Set the observing site location on a profile's mount")
        {
            Options = { siteLatOption, siteLonOption, siteElevOption }
        };
        setSiteCommand.SetAction(SetSiteActionAsync);

        return new Command("profile", "Manage profiles")
        {
            Subcommands =
            {
                listProfilesCommand,
                deleteProfileCommand,
                createProfileCommand,
                addDeviceCommand,
                setMountCommand,
                setGuiderCommand,
                addOtaCommand,
                removeOtaCommand,
                setSiteCommand
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

    internal async Task SetMountActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var deviceId = parseResult.GetRequiredValue(deviceIdArg);
        var uri = await ResolveDeviceUriAsync(deviceId, cancellationToken);
        if (uri is null)
        {
            return;
        }

        var newData = data.Value with { Mount = uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);
    }

    internal async Task SetGuiderActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var deviceId = parseResult.GetRequiredValue(deviceIdArg);
        var uri = await ResolveDeviceUriAsync(deviceId, cancellationToken);
        if (uri is null)
        {
            return;
        }

        var newData = data.Value with { Guider = uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);
    }

    internal async Task SetSiteActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        if (data.Value.Mount == NoneDevice.Instance.DeviceUri)
        {
            consoleHost.WriteError("Profile has no mount configured. Use 'profile set-mount' first.");
            return;
        }

        var lat = parseResult.GetRequiredValue(siteLatOption);
        var lon = parseResult.GetRequiredValue(siteLonOption);
        var elevation = parseResult.GetValue(siteElevOption);

        // Patch the mount URI query params with lat/lon/elevation
        var mountUri = data.Value.Mount;
        var query = System.Web.HttpUtility.ParseQueryString(mountUri.Query);
        query[DeviceQueryKey.Latitude.Key] = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        query[DeviceQueryKey.Longitude.Key] = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (elevation.HasValue)
        {
            query[DeviceQueryKey.Elevation.Key] = elevation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var builder = new UriBuilder(mountUri) { Query = query.ToString() };
        var newData = data.Value with { Mount = builder.Uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Site set to {lat:F4}°{(lat >= 0 ? "N" : "S")}, {lon:F4}°{(lon >= 0 ? "E" : "W")}{(elevation.HasValue ? $", {elevation.Value:F0}m" : "")}");
    }

    internal async Task AddOtaActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var name = parseResult.GetRequiredValue(otaNameArg);
        var focalLength = parseResult.GetRequiredValue(focalLengthOption);
        var cameraId = parseResult.GetRequiredValue(cameraOption);

        var cameraUri = await ResolveDeviceUriAsync(cameraId, cancellationToken);
        if (cameraUri is null)
        {
            return;
        }

        var focuserId = parseResult.GetValue(focuserOption);
        var filterWheelId = parseResult.GetValue(filterWheelOption);
        var coverId = parseResult.GetValue(coverOption);
        var aperture = parseResult.GetValue(apertureOption);
        var opticalDesign = parseResult.GetValue(opticalDesignOption);

        Uri? focuserUri = null;
        if (focuserId is not null)
        {
            focuserUri = await ResolveDeviceUriAsync(focuserId, cancellationToken);
            if (focuserUri is null)
            {
                return;
            }
        }

        Uri? filterWheelUri = null;
        if (filterWheelId is not null)
        {
            filterWheelUri = await ResolveDeviceUriAsync(filterWheelId, cancellationToken);
            if (filterWheelUri is null)
            {
                return;
            }
        }

        Uri? coverUri = null;
        if (coverId is not null)
        {
            coverUri = await ResolveDeviceUriAsync(coverId, cancellationToken);
            if (coverUri is null)
            {
                return;
            }
        }

        var otaData = new OTAData(
            Name: name,
            FocalLength: focalLength,
            Camera: cameraUri,
            Cover: coverUri,
            Focuser: focuserUri,
            FilterWheel: filterWheelUri,
            PreferOutwardFocus: null,
            OutwardIsPositive: null,
            Aperture: aperture,
            OpticalDesign: opticalDesign
        );

        var newData = data.Value with { OTAs = data.Value.OTAs.Add(otaData) };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Added OTA '{name}' (index {newData.OTAs.Length - 1})");
    }

    internal async Task RemoveOtaActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var index = parseResult.GetRequiredValue(otaIndexArg);

        if (index < 0 || index >= data.Value.OTAs.Length)
        {
            consoleHost.WriteError($"OTA index {index} out of range (0..{data.Value.OTAs.Length - 1})");
            return;
        }

        var removedName = data.Value.OTAs[index].Name;
        var newData = data.Value with { OTAs = data.Value.OTAs.RemoveAt(index) };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Removed OTA '{removedName}' (was index {index})");
    }

    internal async Task AddDeviceActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var deviceId = parseResult.GetRequiredValue(deviceIdArg);
        var uri = await ResolveDeviceUriAsync(deviceId, cancellationToken);
        if (uri is null)
        {
            return;
        }

        // Determine device type from discovered devices
        var devices = await consoleHost.ListAllDevicesAsync(DeviceDiscoveryOption.None, cancellationToken);
        var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (device is null)
        {
            consoleHost.WriteError($"No device found with ID '{deviceId}'");
            return;
        }

        var d = data.Value;

        // Mount and guider are profile-level, not per-OTA
        if (device.DeviceType is DeviceType.Mount)
        {
            var newData = d with { Mount = uri };
            await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);
            return;
        }
        if (device.DeviceType is DeviceType.Guider)
        {
            var newData = d with { Guider = uri };
            await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);
            return;
        }

        // Per-OTA device types need a valid OTA index
        if (d.OTAs.Length == 0)
        {
            consoleHost.WriteError("No OTAs in profile. Use 'profile add-ota' first.");
            return;
        }

        var otaIndex = parseResult.GetValue(otaOption);
        if (otaIndex < 0 || otaIndex >= d.OTAs.Length)
        {
            consoleHost.WriteError($"OTA index {otaIndex} out of range (0..{d.OTAs.Length - 1})");
            return;
        }

        var ota = d.OTAs[otaIndex];
        var updatedOta = device.DeviceType switch
        {
            DeviceType.Camera => ota with { Camera = uri },
            DeviceType.Focuser => ota with { Focuser = uri },
            DeviceType.FilterWheel => ota with { FilterWheel = uri },
            DeviceType.CoverCalibrator => ota with { Cover = uri },
            _ => ota
        };

        var result = d with { OTAs = d.OTAs.SetItem(otaIndex, updatedOta) };
        await SaveAndListAsync(selectedProfile, result, parseResult, cancellationToken);
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
        await ListProfilesAsync(cancellationToken);
    }

    // --- Helpers ---

    private async Task<(Profile? Profile, ProfileData? Data)> GetSelectedProfileDataAsync(ParseResult parseResult, CancellationToken ct)
    {
        var allProfiles = await ListProfilesAsync(ct);
        var selectedProfile = parseResult.GetSelected(allProfiles, selectedProfileOption);
        if (selectedProfile is null)
        {
            consoleHost.WriteError("No profile selected. Use --active <name> or ensure exactly one profile exists.");
            return (null, null);
        }
        var data = selectedProfile.Data ?? ProfileData.Empty;
        return (selectedProfile, data);
    }

    private async Task<Uri?> ResolveDeviceUriAsync(string deviceId, CancellationToken ct)
    {
        var devices = await consoleHost.ListAllDevicesAsync(DeviceDiscoveryOption.None, ct);
        var matches = devices.Where(d => d.DeviceId == deviceId).ToList();

        if (matches.Count == 1)
        {
            return matches[0].DeviceUri;
        }

        if (matches.Count == 0)
        {
            consoleHost.WriteError($"No device found with ID '{deviceId}'");
        }
        else
        {
            consoleHost.WriteError($"Multiple devices found with ID '{deviceId}':");
            foreach (var device in matches)
            {
                consoleHost.WriteError($"- {device}");
            }
        }

        return null;
    }

    private async Task SaveAndListAsync(Profile profile, ProfileData newData, ParseResult parseResult, CancellationToken ct)
    {
        var updatedProfile = profile.WithData(newData);
        await updatedProfile.SaveAsync(consoleHost.External, ct);
        await ListProfilesActionAsync(parseResult, ct);
    }

    private Task<IReadOnlyCollection<Profile>> ListProfilesAsync(CancellationToken cancellationToken) =>
        consoleHost.ListDevicesAsync<Profile>(DeviceType.Profile, DeviceDiscoveryOption.Force, cancellationToken);
}
