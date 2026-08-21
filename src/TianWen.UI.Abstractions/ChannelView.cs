using System;

namespace TianWen.UI.Abstractions;

public enum ChannelView
{
    Composite,
    Red,
    Green,
    Blue,
    Channel0,
    Channel1,
    Channel2
}

/// <summary>The one mapping from a channel VIEW to the source channel it puts on screen.</summary>
public static class ChannelViewExtensions
{
    extension(ChannelView view)
    {
        /// <summary>
        /// The source channel this view displays, or <c>null</c> for
        /// <see cref="ChannelView.Composite"/>, which shows every channel at once.
        /// </summary>
        /// <remarks>
        /// <para>Shared by the texture upload and the cursor readout deliberately: they must agree
        /// about which channel is on screen. They did not -- the readout reported R, G and B while
        /// the view was a single channel, naming two channels the user could not see and reading
        /// every float plane per mouse move to do it.</para>
        /// <para>Clamped, because a 2-channel image can reach Channel1 but not Channel2.</para>
        /// </remarks>
        public int? DisplayedSourceChannel(int channelCount) => view switch
        {
            ChannelView.Composite => null,
            ChannelView.Red or ChannelView.Channel0 => 0,
            ChannelView.Green or ChannelView.Channel1 => Math.Min(1, channelCount - 1),
            ChannelView.Blue or ChannelView.Channel2 => Math.Min(2, channelCount - 1),
            _ => throw new InvalidOperationException($"Invalid channel view {view}")
        };
    }
}
