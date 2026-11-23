using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace TianWen.Lib.Imaging.Workbench;

/// <summary>The operations a host has, by id: what an edit's <c>operation</c> is looked up in.</summary>
public sealed class OperationCatalog
{
    private readonly Dictionary<string, IOperation> _byId = new(StringComparer.Ordinal);

    /// <summary>A catalog of <paramref name="operations"/>.</summary>
    /// <exception cref="ArgumentException">Two operations share an id, or one says it is derived and has no
    /// derivation, or that it writes no pixels and has no finding (or the reverse): either would make a step's key or
    /// its handling depend on which one was found. Or one says of its run what its type does not: that several images
    /// go through it in one run (<see cref="OperationDescription.Joint"/>) with no such run, or the reverse; that such
    /// a run takes images by role; or that it gives extra results and cannot be told where they go: its step would
    /// never be whole in the store, and would run on every run; or that a result may be left out of a run several images share.</exception>
    public OperationCatalog(IEnumerable<IOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            var description = operation.Description;
            if ((description.Output is OperationOutputKind.Derived) != (operation is IDerivedOperation)
                || (description.Output is OperationOutputKind.Finding) != (operation is IFindingOperation))
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' says its output is {description.Output} and its type says otherwise", nameof(operations));
            }

            // What is said twice is held to one meaning here: the runner decides by the type, and the docs, a listing
            // and a window by the description.
            if (description.Joint != (operation is IJointOperation))
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' says {(description.Joint ? "several images go through it in one run" : "nothing of a run several images share")}"
                    + " and its type says otherwise", nameof(operations));
            }

            if (description.Joint && !description.ExtraInputs.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' puts several images through one run and also names images it takes by role: the images of its run are all it takes",
                    nameof(operations));
            }

            if (!description.ExtraResults.IsDefaultOrEmpty && operation is not (IJointOperation or IMultiResultOperation))
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' says it gives extra results and has no way to be told where they go", nameof(operations));
            }

            if (description.Joint && description.ExtraResults.Any(static result => result.Optional))
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' puts several images through one run and says a result may be left out: a run several images share gives every result for every image",
                    nameof(operations));
            }

            if (description.AnyNumber && (description.Joint || !description.ExtraInputs.IsDefaultOrEmpty))
            {
                throw new ArgumentException(
                    $"the operation '{description.Id}' says it takes any number of images, and also names the images it takes or shares its run", nameof(operations));
            }

            if (!_byId.TryAdd(description.Id, operation))
            {
                throw new ArgumentException($"two operations are named '{description.Id}'", nameof(operations));
            }
        }
    }

    /// <summary>Every operation, by id.</summary>
    public IReadOnlyList<IOperation> All => [.. _byId.Values.OrderBy(static operation => operation.Description.Id, StringComparer.Ordinal)];

    /// <summary>The operation of this id.</summary>
    public bool TryGet(string id, [NotNullWhen(true)] out IOperation? operation) => _byId.TryGetValue(id, out operation);
}
