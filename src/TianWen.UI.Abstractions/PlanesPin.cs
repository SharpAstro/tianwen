using System;
using System.Threading;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The hold <see cref="AstroImageDocument.EnsurePlanesAsync"/> hands back: while it is undisposed the document's float planes
/// are not released for the memory budget, so a consumer that reads them across several steps never finds them gone.
/// </summary>
public sealed class PlanesPin : IDisposable
{
    /// <summary>The pin of a document that holds nothing to release.</summary>
    public static PlanesPin None { get; } = new(null);

    private AstroImageDocument? _document;

    internal PlanesPin(AstroImageDocument? document) => _document = document;

    /// <summary>Lets the document be released again. Safe to call twice.</summary>
    public void Dispose() => Interlocked.Exchange(ref _document, null)?.Unpin();
}
