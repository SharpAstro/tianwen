using System;
using System.Threading;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A redraw flag that knows what a frame drew. Whoever changes something raises it, and the host clears it after a frame
/// that repainted everything. A plain flag cannot tell what that frame drew from what was asked of it WHILE it was being
/// drawn, and its clear took both: a reading that landed on another thread mid-frame was lost until some other frame
/// happened along, which is what the GUI's settling frame existed to catch, a second late and after every frame whether
/// anything had been lost or not (2026-11-25).
/// </summary>
/// <remarks>
/// <para>Three ways a raise can fall between a frame's start and its clear, and what becomes of each:</para>
/// <list type="bullet">
/// <item><b>from another thread</b> (a load that finished, a run's progress): the frame may have drawn its subject
/// already, without it. It outlives the clear, and the next frame draws it.</item>
/// <item><b>from the draw, for the frame AFTER it</b> (<see cref="RequestFollowingFrame"/>: a texture uploaded in this
/// frame is drawable in the next). It outlives the clear, and buys exactly one frame.</item>
/// <item><b>from the draw, a plain raise</b>: usually for something the frame itself shows (the atlas levels its roll in
/// the draw, every frame), and kept it would buy another frame for ever. It is cleared, and
/// <see cref="ClearAfterFrame"/> says that it was, so a host that cannot vouch for every such raise can still give it a
/// frame of its own.</item>
/// </list>
/// <para>A host that never calls <see cref="BeginDraw"/> has a plain flag.</para>
/// </remarks>
public sealed class RedrawFlag(bool raised = false)
{
    // Benign races, all of them: a raise that lands on either side of a write costs one frame more or is drawn by the
    // frame in progress, and nothing here guards memory.
    private volatile bool _raised = raised;
    private volatile bool _raisedBehindFrame;
    private volatile bool _raisedByTheDraw;
    private volatile int _frameThread;

    // A host whose loop sleeps until something changes, and the thread it runs on. One per process, as its window's
    // loop is.
    private static Action? s_wakeHost;
    private static int s_hostThread;

    /// <summary>
    /// Has every raise from a thread other than the caller's wake the host: for a host whose loop is not asked for a
    /// frame again until its idle interval runs out, an event arrives, or it is woken (SdlVulkan.Renderer's
    /// <c>SdlEventLoop.Wake</c>). Called once, on the loop's thread; null takes it back. A raise on the loop's own thread
    /// wakes nothing: the loop is awake, and asks its check before it next sleeps.
    /// </summary>
    public static void WakeHostOnRaise(Action? wake)
    {
        s_hostThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref s_wakeHost, wake);
    }

    private static void WakeHost()
    {
        if (Volatile.Read(ref s_wakeHost) is { } wake && Environment.CurrentManagedThreadId != s_hostThread)
        {
            wake();
        }
    }

    /// <summary>Whether a frame is asked for.</summary>
    public bool IsRaised => _raised;

    /// <summary>Asks for a frame: something changed.</summary>
    public void Raise()
    {
        var frameThread = _frameThread;
        if (frameThread != 0)
        {
            if (Environment.CurrentManagedThreadId != frameThread)
            {
                _raisedBehindFrame = true;
            }
            else
            {
                _raisedByTheDraw = true;
            }
        }

        _raised = true;
        WakeHost();
    }

    /// <summary>
    /// Asks for the frame AFTER the one being drawn, from inside its draw. Not for a standing reason to draw: each call
    /// buys one frame. Outside a draw it is a plain raise.
    /// </summary>
    public void RequestFollowingFrame()
    {
        _raisedBehindFrame = _frameThread != 0;
        _raised = true;
        WakeHost();
    }

    /// <summary>
    /// Called as a frame starts to draw, on the thread that draws it: what was raised before now is this frame's to
    /// draw, and what is raised from here until the host's clear is judged as the remarks say.
    /// </summary>
    public void BeginDraw()
    {
        _raisedBehindFrame = false;
        _raisedByTheDraw = false;
        _frameThread = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// The host's clear after a frame that repainted everything. What was raised behind that frame stays raised.
    /// </summary>
    /// <returns>Whether a plain raise the draw itself made was DROPPED by this clear: nothing will draw it unless the
    /// host gives it a frame. False when the flag stays raised, since the frame that follows draws everything.</returns>
    public bool ClearAfterFrame()
    {
        _frameThread = 0;
        var byTheDraw = _raisedByTheDraw;
        _raisedByTheDraw = false;
        if (_raisedBehindFrame)
        {
            _raisedBehindFrame = false;
            return false;
        }

        _raised = false;
        return byTheDraw;
    }
}
