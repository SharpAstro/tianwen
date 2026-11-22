using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>
/// The key of a step: what its result is found by in the step store (docs/plans/stacking-workbench.md, "The edit list").
/// </summary>
/// <remarks>
/// <para>A key is a digest of the keys of the step's inputs, the operation's id and version, its settings in canonical
/// form, and what served it. It is computed from keys, so a stored output is never hashed: only an original file is,
/// once, to start the chain (<see cref="OfFile"/>).</para>
/// <para>So a changed setting changes the key of its step and of every step after it, and of no step before it. A step
/// whose key is in the store is not run again, whatever the files' dates say (the plan's rule 2).</para>
/// </remarks>
public static class StepKey
{
    // Part of every key's text. A change to what the text holds or how it is laid out is a new number, so no key of
    // the old layout is ever met by one of the new.
    private const string Layout = "tianwen-step/1";

    /// <summary>
    /// The key of an original file: the digest of its bytes (<see cref="ContentDigest"/>). The whole file, not its data
    /// section alone, since an operation reads the header too (the plate solution, <c>FLUXSCAL</c>). Empty when the
    /// file cannot be read.
    /// </summary>
    /// <remarks>The map sidecars beside a master are not part of it: the stacker writes them with the master, so one
    /// does not change without the other.</remarks>
    public static string OfFile(string path) => ContentDigest.OfFile(path);

    /// <summary>The key of a step.</summary>
    /// <param name="operation">The operation: its id and version go in.</param>
    /// <param name="canonicalSettings">The settings as <see cref="IOperation.TryReadSettings"/> answered them.</param>
    /// <param name="servedBy">What serves the operation here (<see cref="IOperation.ServedBy"/>), empty for none.</param>
    /// <param name="inputKeys">The key of each input, in the order the operation takes them.</param>
    public static string OfStep(OperationDescription operation, string canonicalSettings, string servedBy, IReadOnlyList<string> inputKeys)
    {
        // One fact a line. The settings are JSON a serializer wrote on one line and the keys are digests, so neither
        // holds a line break; the name of what serves is a backend's own string, so its line breaks are taken out.
        var text = new StringBuilder();
        text.Append(Layout).Append('\n');
        text.Append("operation=").Append(operation.Id).Append('@').Append(operation.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("served=").Append(servedBy.ReplaceLineEndings(" ")).Append('\n');
        text.Append("settings=").Append(canonicalSettings).Append('\n');
        foreach (var key in inputKeys)
        {
            text.Append("input=").Append(key).Append('\n');
        }

        return ContentDigest.Format(XxHash128.Hash(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The key of one image's result of a run several images went through (<see cref="IJointOperation"/>): the run's
    /// key, which <see cref="OfStep"/> makes from the images' keys in sorted order, and the image's own.
    /// </summary>
    public static string OfMember(string runKey, string memberKey)
        => Digest("tianwen-member/1\nrun=" + runKey + "\nmember=" + memberKey + "\n");

    /// <summary>The key of an extra result of a step (<see cref="OperationDescription.ExtraResults"/>): the step's key
    /// and the result's name.</summary>
    public static string OfResult(string stepKey, string name)
        => Digest("tianwen-result/1\nstep=" + stepKey + "\nresult=" + name.ReplaceLineEndings(" ") + "\n");

    private static string Digest(string text) => ContentDigest.Format(XxHash128.Hash(Encoding.UTF8.GetBytes(text)));

    /// <summary>A key as a person reads it in a listing: the first twelve digits of its digest.</summary>
    public static string Short(string key)
    {
        var digest = key[(key.IndexOf(':') + 1)..];
        return digest.Length <= 12 ? digest : digest[..12];
    }
}
