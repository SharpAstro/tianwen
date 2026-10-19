using System;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Reframing a running planetary capture on a full-sensor still (#1033): the window the stream ran at as the still was taken,
/// and a PROPOSAL the operator moves on the still (and sizes with the panel's Size stepper), committed to the node or let go.
/// Every window is in the sensor's pixels, unbinned, snapped to the camera's rule. Render thread only.
/// </summary>
public sealed class PlanetaryReframe(int sensorWidth, int sensorHeight, RoiRect liveWindow, RoiConstraints rules)
{
    /// <summary>The still's size: the whole sensor.</summary>
    public int SensorWidth => sensorWidth;

    /// <inheritdoc cref="SensorWidth"/>
    public int SensorHeight => sensorHeight;

    /// <summary>Where the stream's window stood as the still was taken.</summary>
    public RoiRect LiveWindow => liveWindow;

    /// <summary>Where the operator would put it: the live window until moved.</summary>
    public RoiRect Proposal { get; private set; } = liveWindow;

    /// <summary>Whether committing it moves anything.</summary>
    public bool Moves => Proposal != liveWindow;

    /// <summary>Centres the proposal on a point of the still, snapped and kept on the sensor.</summary>
    public void CentreProposalOn(float sensorX, float sensorY)
        => Proposal = rules.Snap(new RoiRect((int)MathF.Round(sensorX - Proposal.Width / 2f), (int)MathF.Round(sensorY - Proposal.Height / 2f),
            Proposal.Width, Proposal.Height));

    /// <summary>Sizes the proposal about its own centre, snapped and kept on the sensor.</summary>
    public void ResizeProposal(int width, int height)
    {
        var (cx, cy) = (Proposal.X + Proposal.Width / 2f, Proposal.Y + Proposal.Height / 2f);
        Proposal = rules.Snap(new RoiRect((int)MathF.Round(cx - width / 2f), (int)MathF.Round(cy - height / 2f), width, height));
    }
}
