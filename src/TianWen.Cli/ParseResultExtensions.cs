using System.CommandLine;
using TianWen.Lib.Devices;

namespace TianWen.Cli;

static class ParseResultExtensions
{
    /// <summary>
    /// The value of a REQUIRED argument, or a named failure if the parser somehow did not supply one.
    /// </summary>
    /// <remarks>
    /// <c>GetValue</c> answers <c>T?</c> for every argument, required or not, so every call site for
    /// a required one used to end in a null-forgiving <c>!</c>. This says the same thing as a CHECK:
    /// same value on the happy path, and a message naming the argument instead of a bare
    /// NullReferenceException if the contract ever breaks.
    /// </remarks>
    internal static T Required<T>(this ParseResult parseResult, Argument<T> argument) where T : class
        => parseResult.GetValue(argument)
            ?? throw new InvalidOperationException(
                $"Required argument '{argument.Name}' was not supplied by the parser.");

    /// <inheritdoc cref="Required{T}(ParseResult, Argument{T})"/>
    internal static T Required<T>(this ParseResult parseResult, Option<T> option) where T : class
        => parseResult.GetValue(option)
            ?? throw new InvalidOperationException(
                $"Required option '{option.Name}' was not supplied by the parser.");

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
