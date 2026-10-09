using System;
using System.IO;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>planetary look --fits</c> over a master left in the camera's colours (the audit on #1343): the look balances the master first, and its
/// FITS must say so, or it reads back as unbalanced and its stretch takes a black point a channel (#1229).
/// </summary>
public sealed class PlanetaryLookFitsTests : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void ALookOfAMasterItHadToBalanceIsWrittenAsBalanced()
    {
        var at = new DateTimeOffset(2024, 12, 15, 12, 56, 42, TimeSpan.Zero);
        var folder = _folders.Create("twlook");
        var masterPath = Path.Combine(folder.FullName, "jupiter_stack.fits");
        PlanetaryColourReadingTests.RenderedJupiter(at, balanced: false).WriteToFitsFile(masterPath);
        Image.TryReadFitsFile(masterPath, out var master).ShouldBeTrue();
        master.ImageMeta.IsColourBalanced.ShouldBeFalse("a stack left in the camera's colours carries no balance");

        var (prepared, refusal) = PlanetaryColourLook.Prepare(master, CatalogIndex.Jupiter, at);
        var ready = prepared.ShouldNotBeNull(refusal);
        ready.Balance.ShouldNotBeNull("the look balanced the master first");
        var (looked, _) = PlanetaryColourLook.Apply(ready.Master, ready.Disk, ColourLook.Boosted);
        var lookPath = Path.Combine(folder.FullName, "jupiter_stack_look.fits");
        looked.ShouldNotBeNull().WriteToFitsFile(lookPath, null, PlanetaryLookSubCommand.Cards(masterPath, ready.Balance, ColourLook.Boosted));

        Image.TryReadFitsFile(lookPath, out var back).ShouldBeTrue();
        back.ImageMeta.IsColourBalanced.ShouldBeTrue("read back, its sky takes ONE black point, as the master the look was made on did");
    }
}
