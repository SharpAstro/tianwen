using System;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Gives the embedded viewer the picture the guided screen wants (<see cref="WorkbenchActions.Wanted"/>), between frames: the
/// original or a step's result is one file request, and a comparison is two in order, so that the viewer's own
/// <see cref="SplitCompareController"/> compares them.
/// </summary>
/// <remarks>
/// A comparison opens the step's input and waits until the viewer has loaded and uploaded it; only then does it ask the viewer
/// to retain the pixels on show (<see cref="ViewerState.RetainBeforePixelsRequested"/>, consumed by the upload that replaces
/// them) and open the result. Once that result is up the split is turned on, in the pixel mode the retained pixels give. What
/// the viewer has been given is remembered by file, so a status that changes nothing about the picture asks for nothing.
/// </remarks>
internal sealed class WorkbenchViewDriver<TSurface>(
    WorkbenchActions actions,
    ViewerState viewerState,
    ViewerController controller,
    ImageRendererBase<TSurface> viewer)
{
    private WorkbenchShown? _shown;
    private string? _displayed;
    private int _stage;

    /// <summary>One step of the loop, after the viewer's file request was handled for this frame.</summary>
    public void Advance()
    {
        if (actions.Wanted() is not { } want)
        {
            return;
        }

        if (!Equals(want, _shown))
        {
            Begin(want);
            return;
        }

        if (_stage == 0 || !ViewerIdle)
        {
            return;
        }

        if (_stage == 1 && want.File is { } result)
        {
            viewerState.RetainBeforePixelsRequested = true;
            Request(result);
            _stage = 2;
        }
        else if (_stage == 2)
        {
            _stage = 0;
            if (viewer.HasBeforeImageTextures && !viewer.Split.IsOn)
            {
                viewer.Split.Toggle(hasBeforePixels: true);
                viewerState.NeedsRedraw = true;
            }
        }
    }

    // Loaded, uploaded and nothing asked for: the pixels on show are the ones last requested.
    private bool ViewerIdle => viewerState.RequestedFilePath is null && !controller.IsLoadPending && !viewerState.NeedsTextureUpdate;

    private void Begin(WorkbenchShown want)
    {
        _shown = want;
        _stage = 0;
        if (viewer.Split.IsOn)
        {
            viewer.Split.Toggle(hasBeforePixels: false);
        }

        if (viewer.HasBeforeImageTextures)
        {
            viewer.ReleaseBeforeImageTextures();
        }

        viewerState.RetainBeforePixelsRequested = false;
        if (want.Kind is WorkbenchViewKind.Comparison)
        {
            if (want.Input is { } input)
            {
                Request(input);
                _stage = 1;
            }
        }
        else if (want.File is { } file)
        {
            Request(file);
        }
    }

    private void Request(string file)
    {
        if (!string.Equals(_displayed, file, StringComparison.OrdinalIgnoreCase))
        {
            _displayed = file;
            viewerState.RequestedFilePath = file;
        }
    }
}
