using System.CommandLine;
using System.Globalization;
using System.Web;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
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

    // Options for set-filters
    private readonly Option<string[]> filtersOption = new("--filters") { Description = "Filter specs as Name:Offset pairs (e.g. Luminance:0 Ha:+21 OIII:-3)", Required = true, AllowMultipleArgumentsPerToken = true };

    // Options for set-camera-defaults
    private readonly Option<int?> cameraGainOption = new("--gain") { Description = "Default camera gain" };
    private readonly Option<int?> cameraOffsetOption = new("--offset") { Description = "Default camera ADC offset" };

    // Options for update-ota
    private readonly Option<string?> updateNameOption = new("--name") { Description = "OTA display name" };
    private readonly Option<int?> updateFocalLengthOption = new("--focal-length") { Description = "Focal length in mm" };
    private readonly Option<int?> updateApertureOption = new("--aperture") { Description = "Aperture in mm" };
    private readonly Option<OpticalDesign?> updateOpticalDesignOption = new("--optical-design") { Description = "Optical design type" };
    private readonly Option<bool?> preferOutwardOption = new("--prefer-outward") { Description = "Prefer outward focus approach" };
    private readonly Option<bool?> outwardIsPositiveOption = new("--outward-is-positive") { Description = "Increasing focuser steps = outward" };

    // Options for set-guider-options
    private readonly Option<string?> pulseGuideSourceOption = new("--pulse-guide-source") { Description = "Pulse guide source: Auto, Camera, or Mount" };
    private readonly Option<bool?> reverseDecOption = new("--reverse-dec-after-flip") { Description = "Reverse DEC corrections after meridian flip" };

    // Options for set-mount-port
    private readonly Option<string> portOption = new("--port") { Description = "Serial port (e.g. COM3, /dev/ttyUSB0)", Required = true };
    private readonly Option<int?> baudOption = new("--baud") { Description = "Baud rate (default depends on mount protocol)" };

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

        var setFiltersCommand = new Command("set-filters", "Set filter names and focus offsets on an OTA's filter wheel")
        {
            Options = { otaOption, filtersOption }
        };
        setFiltersCommand.SetAction(SetFiltersActionAsync);

        var setCameraDefaultsCommand = new Command("set-camera-defaults", "Set default gain/offset on an OTA's camera")
        {
            Options = { otaOption, cameraGainOption, cameraOffsetOption }
        };
        setCameraDefaultsCommand.SetAction(SetCameraDefaultsActionAsync);

        var updateOtaCommand = new Command("update-ota", "Update OTA properties (name, focal length, aperture, optical design, focus direction)")
        {
            Arguments = { otaIndexArg },
            Options = { updateNameOption, updateFocalLengthOption, updateApertureOption, updateOpticalDesignOption, preferOutwardOption, outwardIsPositiveOption }
        };
        updateOtaCommand.SetAction(UpdateOtaActionAsync);

        var setGuiderCameraCommand = new Command("set-guider-camera", "Set the dedicated guider camera (for built-in guider)")
        {
            Arguments = { deviceIdArg }
        };
        setGuiderCameraCommand.SetAction(SetGuiderCameraActionAsync);

        var setGuiderFocuserCommand = new Command("set-guider-focuser", "Set the focuser for the guider camera")
        {
            Arguments = { deviceIdArg }
        };
        setGuiderFocuserCommand.SetAction(SetGuiderFocuserActionAsync);

        var setOagOtaCommand = new Command("set-oag-ota", "Set which OTA hosts the off-axis guider")
        {
            Arguments = { otaIndexArg }
        };
        setOagOtaCommand.SetAction(SetOagOtaActionAsync);

        var setGuiderOptionsCommand = new Command("set-guider-options", "Set guider pulse guide source and DEC reversal")
        {
            Options = { pulseGuideSourceOption, reverseDecOption }
        };
        setGuiderOptionsCommand.SetAction(SetGuiderOptionsActionAsync);

        var setMountPortCommand = new Command("set-mount-port", "Set the serial port and baud rate on the mount")
        {
            Options = { portOption, baudOption }
        };
        setMountPortCommand.SetAction(SetMountPortActionAsync);

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
                updateOtaCommand,
                setSiteCommand,
                setFiltersCommand,
                setCameraDefaultsCommand,
                setGuiderCameraCommand,
                setGuiderFocuserCommand,
                setOagOtaCommand,
                setGuiderOptionsCommand,
                setMountPortCommand
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

    internal async Task SetFiltersActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var otaIndex = parseResult.GetValue(otaOption);
        if (!ValidateOtaIndex(data.Value, otaIndex))
        {
            return;
        }

        var ota = data.Value.OTAs[otaIndex];
        if (ota.FilterWheel is null)
        {
            consoleHost.WriteError($"OTA {otaIndex} ('{ota.Name}') has no filter wheel configured.");
            return;
        }

        var filterSpecs = parseResult.GetRequiredValue(filtersOption);
        var query = HttpUtility.ParseQueryString(ota.FilterWheel.Query);

        // Clear existing filter/offset keys
        var keysToRemove = query.AllKeys.Where(k => k is not null && (k.StartsWith("filter") || k.StartsWith("offset"))).ToArray();
        foreach (var key in keysToRemove)
        {
            query.Remove(key!);
        }

        // Parse and add new filter specs: "Name:Offset" or just "Name" (offset=0)
        for (var i = 0; i < filterSpecs.Length; i++)
        {
            var spec = filterSpecs[i];
            var parts = spec.Split(':');
            var filterName = parts[0];
            var offset = parts.Length > 1 && int.TryParse(parts[1], out var o) ? o : 0;

            query[DeviceQueryKeyExtensions.FilterKey(i + 1)] = filterName;
            query[DeviceQueryKeyExtensions.FilterOffsetKey(i + 1)] = offset.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new UriBuilder(ota.FilterWheel) { Query = query.ToString() };
        var updatedOta = ota with { FilterWheel = builder.Uri };
        var newData = data.Value with { OTAs = data.Value.OTAs.SetItem(otaIndex, updatedOta) };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Set {filterSpecs.Length} filters on OTA {otaIndex} ('{ota.Name}'):");
        for (var i = 0; i < filterSpecs.Length; i++)
        {
            var spec = filterSpecs[i];
            consoleHost.WriteScrollable($"  [{i + 1}] {spec}");
        }
    }

    internal async Task SetCameraDefaultsActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var otaIndex = parseResult.GetValue(otaOption);
        if (!ValidateOtaIndex(data.Value, otaIndex))
        {
            return;
        }

        var ota = data.Value.OTAs[otaIndex];
        var gain = parseResult.GetValue(cameraGainOption);
        var offset = parseResult.GetValue(cameraOffsetOption);

        if (!gain.HasValue && !offset.HasValue)
        {
            consoleHost.WriteError("Specify at least one of --gain or --offset.");
            return;
        }

        var query = HttpUtility.ParseQueryString(ota.Camera.Query);
        if (gain.HasValue)
        {
            query[DeviceQueryKey.Gain.Key] = gain.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (offset.HasValue)
        {
            query[DeviceQueryKey.Offset.Key] = offset.Value.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new UriBuilder(ota.Camera) { Query = query.ToString() };
        var updatedOta = ota with { Camera = builder.Uri };
        var newData = data.Value with { OTAs = data.Value.OTAs.SetItem(otaIndex, updatedOta) };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Camera defaults on OTA {otaIndex} ('{ota.Name}'): gain={gain?.ToString() ?? "(unchanged)"}, offset={offset?.ToString() ?? "(unchanged)"}");
    }

    internal async Task UpdateOtaActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var index = parseResult.GetRequiredValue(otaIndexArg);
        if (!ValidateOtaIndex(data.Value, index))
        {
            return;
        }

        var ota = data.Value.OTAs[index];

        var name = parseResult.GetValue(updateNameOption);
        var focalLength = parseResult.GetValue(updateFocalLengthOption);
        var aperture = parseResult.GetValue(updateApertureOption);
        var opticalDesign = parseResult.GetValue(updateOpticalDesignOption);
        var preferOutward = parseResult.GetValue(preferOutwardOption);
        var outwardIsPositive = parseResult.GetValue(outwardIsPositiveOption);

        var updatedOta = ota with
        {
            Name = name ?? ota.Name,
            FocalLength = focalLength ?? ota.FocalLength,
            Aperture = aperture ?? ota.Aperture,
            OpticalDesign = opticalDesign ?? ota.OpticalDesign,
            PreferOutwardFocus = preferOutward ?? ota.PreferOutwardFocus,
            OutwardIsPositive = outwardIsPositive ?? ota.OutwardIsPositive
        };

        var newData = data.Value with { OTAs = data.Value.OTAs.SetItem(index, updatedOta) };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Updated OTA {index} ('{updatedOta.Name}')");
    }

    internal async Task SetGuiderCameraActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
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

        var newData = data.Value with { GuiderCamera = uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Guider camera set to '{deviceId}'");
    }

    internal async Task SetGuiderFocuserActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
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

        var newData = data.Value with { GuiderFocuser = uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Guider focuser set to '{deviceId}'");
    }

    internal async Task SetOagOtaActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        var index = parseResult.GetRequiredValue(otaIndexArg);
        if (!ValidateOtaIndex(data.Value, index))
        {
            return;
        }

        var newData = data.Value with { OAG_OTA_Index = index };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"OAG set to OTA {index} ('{data.Value.OTAs[index].Name}')");
    }

    internal async Task SetGuiderOptionsActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (selectedProfile, data) = await GetSelectedProfileDataAsync(parseResult, cancellationToken);
        if (selectedProfile is null || data is null)
        {
            return;
        }

        if (data.Value.Guider == NoneDevice.Instance.DeviceUri)
        {
            consoleHost.WriteError("Profile has no guider configured. Use 'profile set-guider' first.");
            return;
        }

        var pulseSource = parseResult.GetValue(pulseGuideSourceOption);
        var reverseDec = parseResult.GetValue(reverseDecOption);

        if (pulseSource is null && reverseDec is null)
        {
            consoleHost.WriteError("Specify at least one of --pulse-guide-source or --reverse-dec-after-flip.");
            return;
        }

        var query = HttpUtility.ParseQueryString(data.Value.Guider.Query);
        if (pulseSource is not null)
        {
            query[DeviceQueryKey.PulseGuideSource.Key] = pulseSource;
        }
        if (reverseDec.HasValue)
        {
            query[DeviceQueryKey.ReverseDecAfterFlip.Key] = reverseDec.Value.ToString().ToLowerInvariant();
        }

        var builder = new UriBuilder(data.Value.Guider) { Query = query.ToString() };
        var newData = data.Value with { Guider = builder.Uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);
    }

    internal async Task SetMountPortActionAsync(ParseResult parseResult, CancellationToken cancellationToken)
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

        var port = parseResult.GetRequiredValue(portOption);
        var baud = parseResult.GetValue(baudOption);

        var query = HttpUtility.ParseQueryString(data.Value.Mount.Query);
        query[DeviceQueryKey.Port.Key] = port;
        if (baud.HasValue)
        {
            query[DeviceQueryKey.Baud.Key] = baud.Value.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new UriBuilder(data.Value.Mount) { Query = query.ToString() };
        var newData = data.Value with { Mount = builder.Uri };
        await SaveAndListAsync(selectedProfile, newData, parseResult, cancellationToken);

        consoleHost.WriteScrollable($"Mount port set to '{port}'{(baud.HasValue ? $" at {baud.Value} baud" : "")}");
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

    private bool ValidateOtaIndex(ProfileData data, int otaIndex)
    {
        if (data.OTAs.Length == 0)
        {
            consoleHost.WriteError("No OTAs in profile. Use 'profile add-ota' first.");
            return false;
        }
        if (otaIndex < 0 || otaIndex >= data.OTAs.Length)
        {
            consoleHost.WriteError($"OTA index {otaIndex} out of range (0..{data.OTAs.Length - 1})");
            return false;
        }
        return true;
    }

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
