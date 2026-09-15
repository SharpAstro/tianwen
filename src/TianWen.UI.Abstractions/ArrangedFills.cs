using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Reading a rect back out of an arranged tree by the key its <see cref="Layout.Content.Fill"/> leaf
    /// declared -- how a tab that hands a docked region to an imperative painter asks the tree where that
    /// region ended up.
    /// <para>
    /// Shared rather than copied per tab because the lookup IS the contract of a keyed Fill: the tree
    /// states where a region is, and every consumer must ask the same way. Two private copies is the
    /// shape that lets one of them drift into answering a rect the tree no longer arranges.
    /// </para>
    /// </summary>
    internal static class ArrangedFills
    {
        /// <summary>
        /// Arranged rect of the keyed <see cref="Layout.Content.Fill"/> leaf, or an empty rect when the
        /// leaf is not in the arrangement. Empty is a real answer rather than a failure: a leaf that
        /// <see cref="Layout.Node.CollapseBelow"/> collapsed away, or a strip a C# branch left out of the
        /// tree this frame, is absent by design and the caller skips painting it.
        /// </summary>
        public static RectF32 RectOf(ImmutableArray<Layout.ArrangedNode<float>> arranged, string key)
        {
            foreach (var a in arranged)
            {
                if (a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill fill } && fill.Key == key)
                {
                    return new RectF32(a.Bounds.X, a.Bounds.Y, a.Bounds.Width, a.Bounds.Height);
                }
            }

            return default;
        }
    }
}
