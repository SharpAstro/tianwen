using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace TianWen.AI.Imaging;

/// <summary>
/// The one rule for a dataset verb's <c>--session</c>: case-insensitive substrings of a session id, several per flag.
/// A value too short to name a session matches nearly every id ("e" or "t" took 201 of 201 sessions, #1778, when a
/// PowerShell splat turned <c>--kept-cores</c> into twelve one-letter values of <c>--session</c>), so it is refused
/// by name, and what a filter selected is said before any work starts.
/// </summary>
public static class SessionFilter
{
    /// <summary>The fewest characters a value may have; a session id is a camera, a target and a night.</summary>
    public const int MinLength = 3;

    /// <summary>Whether <paramref name="filters"/> hold no value shorter than <see cref="MinLength"/>; the error names each one that is.</summary>
    public static bool TryValidate(IEnumerable<string>? filters, [NotNullWhen(false)] out string? error)
    {
        // An options record's unset ImmutableArray is default, and enumerating it boxed throws: it is no filter.
        if (filters is null or ImmutableArray<string> { IsDefault: true })
        {
            error = null;
            return true;
        }

        var tooShort = filters.Where(static f => f.Trim().Length < MinLength).ToArray();
        if (tooShort.Length == 0)
        {
            error = null;
            return true;
        }

        var named = string.Join(", ", tooShort.Select(static f => $"'{f}'"));
        error = $"--session takes case-insensitive substrings of at least {MinLength} characters, and {named} " +
                $"{(tooShort.Length == 1 ? "is" : "are")} shorter: a short value matches nearly every session. " +
                "Check that no other option's values were split into this one (a PowerShell splat does that).";
        return false;
    }

    /// <summary>Throws an <see cref="ArgumentException"/> naming the short values; the guard for a caller that never met the CLI's validator.</summary>
    public static void Require(IEnumerable<string>? filters)
    {
        if (!TryValidate(filters, out var error))
        {
            throw new ArgumentException(error, nameof(filters));
        }
    }

    /// <summary>Whether <paramref name="sessionId"/> holds any of <paramref name="filters"/>; no filters selects everything.</summary>
    public static bool Matches(string sessionId, ImmutableArray<string> filters) =>
        filters.IsDefaultOrEmpty || filters.Any(f => sessionId.Contains(f.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids <paramref name="filters"/> select from <paramref name="sessionIds"/>, in the order given.</summary>
    public static List<string> Select(IEnumerable<string> sessionIds, ImmutableArray<string> filters) =>
        [.. sessionIds.Where(id => Matches(id, filters))];

    /// <summary>
    /// The lines that say what was selected: the count of the total, then the ids (every one when a filter is
    /// given, else only the count, since "all of them" needs no list). <paramref name="matched"/> is what the filter
    /// matched when a cap (<paramref name="cap"/>, <c>--max-sessions</c>) cut <paramref name="selected"/> to fewer, so
    /// the count never claims more sessions than run.
    /// </summary>
    public static IReadOnlyList<string> Describe(string verb, IReadOnlyList<string> selected, int total, ImmutableArray<string> filters,
        int? matched = null, int cap = 0)
    {
        var filtered = !filters.IsDefaultOrEmpty;
        var count = matched ?? selected.Count;
        var capped = cap > 0 && count > selected.Count ? $", the first {selected.Count} run (--max-sessions {cap})" : "";
        var lines = new List<string>
        {
            filtered
                ? $"[{verb}] --session {string.Join(" ", filters.Select(static f => $"'{f}'"))} selects {count} of {total} sessions{capped}"
                : $"[{verb}] no --session filter: {count} of {total} sessions{capped}",
        };
        if (filtered || capped.Length > 0)
        {
            lines.AddRange(selected.Select(id => $"[{verb}]   {id}"));
        }
        return lines;
    }

    /// <summary>
    /// Validates, selects and announces in one call: refuses a short value, then says <see cref="Describe"/> so the
    /// selection is on record before the caller does any work.
    /// </summary>
    public static List<string> SelectAndAnnounce(string verb, IReadOnlyCollection<string> sessionIds, ImmutableArray<string> filters,
        ILogger? logger, Action<string>? say = null)
    {
        Require(filters);
        var selected = Select(sessionIds, filters);
        Announce(verb, selected, sessionIds.Count, filters, logger, say);
        return selected;
    }

    /// <summary>
    /// Says <see cref="Describe"/>, for a caller that selected by its own walk (a manifest read row by row) or capped
    /// the selection. The lines go to <paramref name="say"/>, which a CLI verb points at its console (a Release
    /// build's console logger shows warnings only, so the logger alone would leave the selection in the file log),
    /// and to <paramref name="logger"/>.
    /// </summary>
    public static void Announce(string verb, IReadOnlyList<string> selected, int total, ImmutableArray<string> filters,
        ILogger? logger, Action<string>? say = null, int? matched = null, int cap = 0)
    {
        if (logger is null && say is null)
        {
            return;
        }

        foreach (var line in Describe(verb, selected, total, filters, matched, cap))
        {
            say?.Invoke(line);
            logger?.LogInformation("{Line}", line);
        }
    }
}
