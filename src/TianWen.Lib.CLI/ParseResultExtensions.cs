using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Lib.CLI;

static class ParseResultExtensions
{
    internal static Profile? GetSelected(this ParseResult parseResult, IReadOnlyCollection<Profile> allProfiles, Option<string?> selectedProfileOption)
    {
        var selectedOptionValue = parseResult.GetValue(selectedProfileOption);
        if (Guid.TryParse(selectedOptionValue, out var selectedId))
        {
            return allProfiles.SingleOrDefault(p => p.ProfileId == selectedId);
        }
        else if (allProfiles.Count is 1 && string.IsNullOrEmpty(selectedOptionValue))
        {
            return allProfiles.Single();
        }
        else
        {
            var possibleProfiles = allProfiles.Where(p => string.Equals(p.DisplayName, selectedOptionValue, StringComparison.CurrentCultureIgnoreCase)).ToList();

            return possibleProfiles.Count is 1 ? possibleProfiles.Single() : null;
        }
    }
}
