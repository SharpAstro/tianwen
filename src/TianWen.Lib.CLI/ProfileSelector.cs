using Console.Lib;
using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

/// <summary>
/// Resolves the active profile for commands that need one. Handles:
/// 1. --active CLI arg → use that profile
/// 2. Exactly one profile → auto-select
/// 3. Zero profiles → first-run creation (interactive) or error (non-interactive)
/// 4. Multiple profiles → MenuBase picker (interactive) or error (non-interactive)
/// </summary>
internal class ProfileSelector(IConsoleHost consoleHost, Option<string?> selectedProfileOption)
{
    /// <summary>
    /// Resolves the active profile. Returns null if no profile could be resolved.
    /// </summary>
    public async Task<Profile?> ResolveProfileAsync(ParseResult parseResult, bool interactive, CancellationToken ct)
    {
        var allProfiles = await consoleHost.ListDevicesAsync<Profile>(
            DeviceType.Profile, DeviceDiscoveryOption.Force, ct);

        // Try non-interactive resolution first (works for all modes)
        var resolved = parseResult.GetSelected(allProfiles, selectedProfileOption);
        if (resolved is not null)
        {
            return resolved;
        }

        // If --active was explicitly provided but didn't match, that's an error
        var explicitName = parseResult.GetValue(selectedProfileOption);
        if (!string.IsNullOrEmpty(explicitName))
        {
            consoleHost.WriteError($"Profile not found: '{explicitName}'");
            return null;
        }

        // Zero profiles
        if (allProfiles.Count == 0)
        {
            if (interactive)
            {
                return await FirstRunWizardAsync(ct);
            }

            consoleHost.WriteError("No profiles found. Create one with: tianwen profile create <name>");
            return null;
        }

        // Multiple profiles, no --active
        if (interactive)
        {
            return await PickProfileInteractiveAsync(allProfiles, ct);
        }

        consoleHost.WriteError($"Multiple profiles found ({allProfiles.Count}). Use --active <name> to select one:");
        foreach (var p in allProfiles)
        {
            consoleHost.WriteError($"  - {p.DisplayName} ({p.ProfileId})");
        }
        return null;
    }

    private async Task<Profile?> PickProfileInteractiveAsync(IReadOnlyCollection<Profile> profiles, CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        await terminal.InitAsync();

        var profileList = profiles.ToArray();
        var names = profileList.Select(p => p.DisplayName).ToArray();

        var menu = new ProfilePickerMenu(terminal, consoleHost.TimeProvider, names);
        var index = await menu.ShowAsync(ct);

        if (index < 0 || index >= profileList.Length)
        {
            return null;
        }

        return profileList[index];
    }

    private async Task<Profile?> FirstRunWizardAsync(CancellationToken ct)
    {
        var terminal = consoleHost.Terminal;
        await terminal.InitAsync();

        // Step 1: Get profile name
        System.Console.Error.Write("Welcome! Let's create your first equipment profile.\nProfile name: ");
        var name = System.Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            consoleHost.WriteError("Profile name cannot be empty.");
            return null;
        }

        // Create profile
        var profile = new Profile(Guid.NewGuid(), name, ProfileData.Empty);
        await profile.SaveAsync(consoleHost.External, ct);
        consoleHost.WriteScrollable($"Created profile '{name}' ({profile.ProfileId})");

        // Step 2: Discover devices
        consoleHost.WriteScrollable("\nDiscovering devices...");
        var devices = await consoleHost.ListAllDevicesAsync(DeviceDiscoveryOption.Force, ct);
        var deviceList = devices.Where(d => d.DeviceType is not DeviceType.Profile).ToArray();

        if (deviceList.Length == 0)
        {
            consoleHost.WriteScrollable("No devices found. You can add devices later with 'tianwen profile add'.");
            return profile;
        }

        consoleHost.WriteScrollable($"Found {deviceList.Length} devices:");
        for (var i = 0; i < deviceList.Length; i++)
        {
            consoleHost.WriteScrollable($"  [{i}] {deviceList[i].DeviceType}: {deviceList[i].DisplayName}");
        }

        // Step 3: Pick mount
        var mounts = deviceList.Where(d => d.DeviceType is DeviceType.Mount).ToArray();
        if (mounts.Length > 0)
        {
            var mountNames = mounts.Select(m => m.DisplayName).ToArray();
            var mountMenu = new ProfilePickerMenu(terminal, consoleHost.TimeProvider, mountNames, "Select mount");
            var mountIdx = await mountMenu.ShowAsync(ct);
            if (mountIdx >= 0 && mountIdx < mounts.Length)
            {
                var data = profile.Data ?? ProfileData.Empty;
                profile = profile.WithData(data with { Mount = mounts[mountIdx].DeviceUri });
                await profile.SaveAsync(consoleHost.External, ct);
            }
        }

        // Step 4: Pick guider
        var guiders = deviceList.Where(d => d.DeviceType is DeviceType.Guider).ToArray();
        if (guiders.Length > 0)
        {
            var guiderNames = guiders.Select(g => g.DisplayName).ToArray();
            var guiderMenu = new ProfilePickerMenu(terminal, consoleHost.TimeProvider, guiderNames, "Select guider");
            var guiderIdx = await guiderMenu.ShowAsync(ct);
            if (guiderIdx >= 0 && guiderIdx < guiders.Length)
            {
                var data = profile.Data ?? ProfileData.Empty;
                profile = profile.WithData(data with { Guider = guiders[guiderIdx].DeviceUri });
                await profile.SaveAsync(consoleHost.External, ct);
            }
        }

        consoleHost.WriteScrollable($"\nProfile '{name}' is ready. Add OTAs with 'tianwen profile add-ota'.");
        return profile;
    }

    /// <summary>
    /// Simple MenuBase subclass for picking from a string list.
    /// </summary>
    private sealed class ProfilePickerMenu(
        IVirtualTerminal terminal,
        ITimeProvider timeProvider,
        string[] items,
        string title = "Select profile"
    ) : MenuBase<int>(terminal, timeProvider.System)
    {
        protected override async Task<int> ShowAsyncCore(CancellationToken cancellationToken)
        {
            return await ShowMenuAsync(title, "Choose:", items, cancellationToken);
        }
    }
}
