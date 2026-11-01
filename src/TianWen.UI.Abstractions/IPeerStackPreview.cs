namespace TianWen.UI.Abstractions;

/// <summary>
/// A preview source that can also show the OTHER stack of the same capture beside it, so the viewer's A | B can compare the Live
/// rolling stack with the Full one (<see cref="SplitCompare.LiveVsFull"/>, #1883, the names of #1877). Implemented by
/// <see cref="LiveStackPreviewSource"/>: whichever of the two is on show is the right half, the other the left.
/// </summary>
/// <remarks>
/// Render thread only, like the source. The controller attaches the peer and drives it (follow, sharpen, publish) only while
/// <see cref="PeerWanted"/> says it is wanted, so a view nobody compares pays nothing for it. The peer is sharpened by the same routine
/// as this one (<see cref="LiveStackPreviewSource.SetSharpen"/>) with the dials on show, never by a second path.
/// </remarks>
public interface IPeerStackPreview
{
    /// <summary>What this view is called in a comparison: "Live" or "Full".</summary>
    string OwnName { get; }

    /// <summary>What the other view is called.</summary>
    string PeerName { get; }

    /// <summary>True when the other stack exists, built this session or beside the SER (it may still be loading).</summary>
    bool PeerKnown { get; }

    /// <summary>True when the other stack has a master to show.</summary>
    bool CanShowPeer { get; }

    /// <summary>The other stack's sharpened master on show, or null until it is built (or while it is not wanted).</summary>
    IPreviewSource? Peer { get; }

    /// <summary>Identity of <see cref="Peer"/>: moves each time the peer publishes a master (a new stack or a re-sharpen) or a new placement.</summary>
    int PeerGeneration { get; }

    /// <summary>Where <see cref="Peer"/> is drawn from this master's origin, in this master's pixels, so the two disks coincide.</summary>
    (float X, float Y) PeerOffset { get; }

    /// <summary>True while the comparison wants the peer: what the controller reads to drive it.</summary>
    bool PeerWanted { get; }

    /// <summary>Says whether the peer is wanted. Idempotent; call every frame it is compared, and with false when it is not.</summary>
    void WantPeer(bool wanted);
}
