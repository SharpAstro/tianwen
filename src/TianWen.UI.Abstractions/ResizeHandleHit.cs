using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// App-specific <see cref="HitResult"/> variant: the user grabbed a drag
/// handle for a resizable panel (currently only the file list sidebar). The
/// <paramref name="Id"/> string identifies which panel is being resized.
/// </summary>
public sealed record ResizeHandleHit(string Id) : HitResult;
