using Pastel;
using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

internal class ProfileSubCommand(IConsoleHost consoleHost, Option<string?> selectedProfileOption)
{
    private readonly Argument<string> profileNameOrIdArg = new Argument<string>("profileNameOrId") { Description = "Name or ID of the profile" };
    private readonly Argument<string> profileNameArg = new Argument<string>("profileName") { Description = "Name of the new profile" };

    public Command Build()
    {
        var listProfilesCommand = new Command("list", "List all profiles");
        listProfilesCommand.SetAction(ListProfilesAsync);

        var deleteProfileCommand = new Command("delete", "Delete a profile")
        {
            Arguments = { profileNameOrIdArg }
        };
        deleteProfileCommand.SetAction(DeleteProfileAsync);

        var createProfileCommand = new Command("create", "Create a new empty profile")
        {
            Arguments = { profileNameArg }
        };
        createProfileCommand.SetAction(CreateProfileAsync);

        var addDeviceCommand = new Command("add", "Add a device to a profile")
        {
            Arguments = { profileNameArg }
        };

        return new Command("profile", "Manage profiles")
        {
            Subcommands = {
                listProfilesCommand,
                deleteProfileCommand,
                createProfileCommand
            }
        };
    }

    internal async Task CreateProfileAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var profileName = parseResult.GetRequiredValue(profileNameArg);

        var newProfile = new Profile(Guid.NewGuid(), profileName, ProfileData.Empty);
        await newProfile.SaveAsync(consoleHost.External);
        Console.WriteLine($"Created new profile '{newProfile.DisplayName}' with ID {newProfile.ProfileId}");
    }

    internal async Task ListProfilesAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var allProfiles = await ListProfilesAsync(cancellationToken);

        var selectedProfile = parseResult.GetSelected(allProfiles, selectedProfileOption);

        foreach (var profile in allProfiles)
        {
            Console.WriteLine();
            if (profile.ProfileId == selectedProfile?.ProfileId)
            {
                Console.Write("* ".Pastel(ConsoleColor.White));
            }
            else
            {
                Console.Write("  ");
            }
            Console.WriteLine(profile.Detailed(consoleHost.DeviceUriRegistry));
        }
    }

    internal async Task AddDeviceAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {

    }

    internal async Task DeleteProfileAsync(ParseResult parseResult, CancellationToken cancellationToken)
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
                Console.Error.WriteLine($"No profile found with ID '{profileId}'");
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
                Console.Error.WriteLine($"Multiple profiles found with name '{profileNameOrId}':");
                foreach (var profile in matchingProfiles)
                {
                    Console.Error.WriteLine($"- {profile.ProfileId}");
                }
                return;
            }
            else
            {
                Console.Error.WriteLine($"No profiles found with name '{profileNameOrId}'");
                return;
            }
        }

        profileToDelete.Delete(consoleHost.External);

        Console.WriteLine($"Deleted profile '{profileToDelete.DisplayName}' ({profileToDelete.ProfileId})");

        // refresh cache
        var profilesAfterDelete = await ListProfilesAsync(cancellationToken);
    }

    private Task<IReadOnlyCollection<Profile>> ListProfilesAsync(CancellationToken cancellationToken) =>
        consoleHost.ListDevicesAsync<Profile>(DeviceType.Profile, DeviceDiscoveryOption.Force, cancellationToken);
}
