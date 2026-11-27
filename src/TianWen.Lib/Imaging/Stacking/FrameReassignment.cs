using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// One file the person says is another kind of frame than the scan took it for (#2553): <see cref="Path"/> is the
/// file's place inside the capture folder (forward slashes, never a drive or a folder above the capture), and
/// <see cref="Kind"/> what it is: a light, dark, flat, bias or dark flat, or <see cref="FrameType.None"/> to leave it
/// out of the stack altogether. Kept in the project (the stack step's settings), so it changes the step's key, and taken
/// by <see cref="StackingPipeline"/>'s scan, which both the step and <c>tianwen stack --reassign</c> run.
/// </summary>
/// <remarks>Both members are settable and the last one is a non-nullable value, as the settings' JSON needs.</remarks>
public sealed record FrameReassignment
{
    /// <summary>The file's place inside the capture folder, with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What the file is; <see cref="FrameType.None"/> leaves it out of the stack.</summary>
    public FrameType Kind { get; set; }

    /// <summary>The kinds a reassignment may name, in the order a person is offered them.</summary>
    public static ImmutableArray<FrameType> Kinds { get; } = [FrameType.Light, FrameType.Dark, FrameType.Flat, FrameType.Bias, FrameType.DarkFlat, FrameType.None];

    /// <summary>A kind as a person types it: <c>light</c>, <c>dark</c>, <c>flat</c>, <c>bias</c>, <c>darkflat</c>, <c>ignore</c>.</summary>
    public static string WordOf(FrameType kind) => kind == FrameType.None ? "ignore" : kind.ToString().ToLowerInvariant();

    /// <summary>The kind a word names (<see cref="WordOf"/>, in any case); false for a word that names none.</summary>
    public static bool TryParseKind(string word, out FrameType kind)
    {
        foreach (var candidate in Kinds)
        {
            if (string.Equals(WordOf(candidate), word.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = FrameType.None;
        return false;
    }

    /// <summary>The kinds as a sentence, for a refusal.</summary>
    public static string KindWords => string.Join(", ", Kinds.Select(WordOf));

    /// <summary>The form on a command line: <c>path=kind</c>.</summary>
    public string Format() => Normalise(Path) + "=" + WordOf(Kind);

    /// <summary>Reads <c>path=kind</c>; false, with the reason, for anything else.</summary>
    public static bool TryParse(string text, out FrameReassignment? reassignment, out string? refusal)
    {
        reassignment = null;
        var at = text.LastIndexOf('=');
        if (at <= 0)
        {
            refusal = $"'{text}' is not path=kind, for example Lights/frame_0001.fits=dark";
            return false;
        }

        if (!TryParseKind(text[(at + 1)..], out var kind))
        {
            refusal = $"'{text[(at + 1)..]}' is not a kind: say one of {KindWords}";
            return false;
        }

        reassignment = new FrameReassignment { Path = Normalise(text[..at]), Kind = kind };
        refusal = null;
        return true;
    }

    /// <summary>A place inside the capture folder written the one way: forward slashes, no leading <c>./</c> or slash.</summary>
    public static string Normalise(string path)
    {
        var slashed = path.Trim().Replace('\\', '/');
        while (slashed.StartsWith("./", StringComparison.Ordinal))
        {
            slashed = slashed[2..];
        }

        return slashed.TrimStart('/');
    }

    /// <summary>The place of <paramref name="file"/> inside <paramref name="captureFolder"/>, in the form
    /// <see cref="Path"/> keeps; null when the file is not under it.</summary>
    public static string? PlaceOf(string captureFolder, string file)
    {
        var relative = System.IO.Path.GetRelativePath(captureFolder, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) ? null : Normalise(relative);
    }

    /// <summary>A refusal in words for a list that names a file twice, names none or a kind a stack does not take; null when it is sound.</summary>
    public static string? RefusalOf(ImmutableArray<FrameReassignment> reassignments)
    {
        if (reassignments.IsDefaultOrEmpty)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var each in reassignments)
        {
            if (Normalise(each.Path).Length == 0)
            {
                return "a reassignment names no file";
            }

            if (!Kinds.Contains(each.Kind))
            {
                return $"{each.Path} is reassigned to {each.Kind}, which is no kind of frame a stack takes: say one of {KindWords}";
            }

            if (!seen.Add(Normalise(each.Path)))
            {
                return $"{each.Path} is reassigned twice: say one kind for a file";
            }
        }

        return null;
    }

    /// <summary>The list as a lookup by place, ignoring case.</summary>
    public static Dictionary<string, FrameType> LookupOf(ImmutableArray<FrameReassignment> reassignments)
    {
        var lookup = new Dictionary<string, FrameType>(StringComparer.OrdinalIgnoreCase);
        if (!reassignments.IsDefaultOrEmpty)
        {
            foreach (var each in reassignments)
            {
                lookup[Normalise(each.Path)] = each.Kind;
            }
        }

        return lookup;
    }

    /// <summary>The list in one order whatever order it was made in (by place), so the same reassignments are one step key.</summary>
    public static ImmutableArray<FrameReassignment> Sorted(IEnumerable<FrameReassignment> reassignments)
    {
        var sorted = reassignments
            .OrderBy(static each => Normalise(each.Path), StringComparer.OrdinalIgnoreCase)
            .Select(static each => new FrameReassignment { Path = Normalise(each.Path), Kind = each.Kind })
            .ToImmutableArray();
        // An ImmutableArray is equal to another only when both hold the same array instance, so nothing reassigned is the
        // shared empty one, which is also what StackingOptions.Reassigned defaults to: a bare stack line equals the record's defaults.
        return sorted.IsEmpty ? ImmutableArray<FrameReassignment>.Empty : sorted;
    }
}
