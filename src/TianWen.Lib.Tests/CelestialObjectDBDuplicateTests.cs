using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A catalogue entry marked as a DUPLICATE names the entry it duplicates, and <c>TryLookupByIndex</c> follows it.
/// It used to follow by calling itself, guarded only against an entry naming itself, so a cycle in the baked
/// cross-index table (A names B, B names A) recursed until the stack overflowed, on every host and the browser's
/// first (the follow-up to #953). The baked catalogue holds no cycle, and its longest chain is one hop (measured:
/// 642 of its 245,078 names need that hop, none a second), so these build what it does not hold.
/// </summary>
public class CelestialObjectDBDuplicateTests
{
    private static CatalogIndex Ngc(int number)
        => CatalogUtils.TryGetCleanedUpCatalogName($"NGC {number}", out var index) ? index : throw new InvalidOperationException($"NGC {number}");

    private static CelestialObject Entry(CatalogIndex index, ObjectType type)
        => new CelestialObject(index, type, 1.0, 2.0, Constellation.Andromeda, Half.NaN, Half.NaN, Half.NaN, new HashSet<string>());

    /// <summary>A catalogue of <paramref name="duplicates"/> duplicates in a chain, ending on a galaxy unless it loops back.</summary>
    private static (CelestialObjectDB Db, CatalogIndex First, CatalogIndex Last) Chain(int duplicates, bool loopBack = false)
    {
        var db = new CelestialObjectDB();
        for (var i = 1; i <= duplicates; i++)
        {
            var next = i < duplicates ? Ngc(i + 1) : loopBack ? Ngc(1) : Ngc(i + 1);
            db.AddEntry(Entry(Ngc(i), ObjectType.Duplicate), duplicates: next);
        }
        if (!loopBack)
        {
            db.AddEntry(Entry(Ngc(duplicates + 1), ObjectType.Galaxy));
        }
        return (db, Ngc(1), Ngc(duplicates + 1));
    }

    [Fact]
    public void ACycleOfDuplicatesIsAFailedLookupNotAStackOverflow()
    {
        var (db, first, _) = Chain(duplicates: 2, loopBack: true);

        db.TryLookupByIndex(first, out _).ShouldBeFalse("NGC 1 names NGC 2, which names NGC 1: there is nothing to name");
    }

    [Fact]
    public void AChainOfDuplicatesIsFollowedToTheEntryItNames()
    {
        var (db, first, last) = Chain(duplicates: 3);

        db.TryLookupByIndex(first, out var found).ShouldBeTrue();
        found.Index.ShouldBe(last);
        found.ObjectType.ShouldBe(ObjectType.Galaxy);
    }

    [Fact]
    public void AChainAsLongAsTheBoundIsFollowedAndOneLongerIsNot()
    {
        var (withinBound, first, last) = Chain(duplicates: CelestialObjectDB.MaxDuplicateHops);
        withinBound.TryLookupByIndex(first, out var found).ShouldBeTrue();
        found.Index.ShouldBe(last);

        var (pastBound, pastFirst, _) = Chain(duplicates: CelestialObjectDB.MaxDuplicateHops + 1);
        pastBound.TryLookupByIndex(pastFirst, out _).ShouldBeFalse();
    }
}
