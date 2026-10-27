using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>How turning Enhance back OFF gets the original pixels back.</summary>
public enum EnhanceRevert
{
    /// <summary>Swap the retained pre-enhance document back in. Instant, costs its memory.</summary>
    Retained,

    /// <summary>Re-open the file. Costs a read and a re-stat, costs no memory.</summary>
    Reload,
}

/// <summary>
/// Decides whether a pre-enhance document is worth holding on to, so that turning Enhance off is a
/// reference swap rather than a re-read.
/// </summary>
/// <remarks>
/// <para>Hybrid because the two costs are not comparable: retaining is a REFERENCE, not a copy -- the
/// document is already alive for the duration of the run, so keeping it is declining to drop it --
/// but on a large master that reference pins ~95 MB for as long as the enhanced view is displayed.
/// Reloading pins nothing and costs a second or two off disk.</para>
/// <para>The budget is on the retained image's own footprint rather than on free system memory:
/// available memory is a number that changes under you between the decision and the consequence,
/// and a policy that flips based on what some other process is doing makes the viewer behave
/// differently on identical input.</para>
/// </remarks>
public static class EnhanceRevertPolicy
{
    /// <summary>
    /// Largest pre-enhance image worth holding, in bytes. 128 MB admits a 3-channel 4K master
    /// (~95 MB) and excludes the large-sensor mosaics where the same reference would be several
    /// hundred megabytes for a revert the user may never press.
    /// </summary>
    public const long RetainBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Bytes a document's linear image occupies -- what retaining it actually costs.</summary>
    public static long FootprintBytes(Image image)
        => (long)image.Width * image.Height * image.ChannelCount * sizeof(float);

    /// <summary>
    /// Whether <paramref name="source"/> should be retained for revert, given whether its file can be
    /// re-opened.
    /// </summary>
    public static EnhanceRevert Decide(Image source, bool canReload)
    {
        if (FootprintBytes(source) <= RetainBudgetBytes)
        {
            return EnhanceRevert.Retained;
        }

        // Over budget: reload if we can, and retain anyway if we cannot. Holding a large image beats
        // an Enhance that cannot be undone -- the toggle promising a state it will not return to is
        // worse than the memory, and it is the one case where the budget must yield.
        return canReload ? EnhanceRevert.Reload : EnhanceRevert.Retained;
    }

    /// <summary>
    /// Whether a LIVE document's float planes are worth keeping resident once its load-time readers are done,
    /// or are dropped and read back from the file by whatever needs pixels next (<see cref="PlaneResidency.Release"/>).
    /// </summary>
    /// <remarks>
    /// The same budget as <see cref="Decide"/>, never a second constant: an image too big to hold a pre-enhance
    /// REFERENCE to is too big to hold twice over in a viewer that only displays it. Where the file cannot give
    /// the planes back, the budget yields and they stay.
    /// </remarks>
    public static PlaneResidency DecidePlanes(Image image, bool canReload, long budgetBytes = RetainBudgetBytes)
        => canReload && FootprintBytes(image) > budgetBytes ? PlaneResidency.Release : PlaneResidency.Keep;
}

/// <summary>What happens to a live document's float planes after its load-time readers.</summary>
public enum PlaneResidency
{
    /// <summary>They stay resident.</summary>
    Keep,

    /// <summary>They are dropped and restored from the file on demand (<c>AstroImageDocument.EnsurePlanesAsync</c>).</summary>
    Release,
}
