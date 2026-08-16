using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The object-overlay pass runs on EVERY frame regardless of the [O] and [D] toggles, because a
    /// pinned planner target is a landmark that must stay visible with its layer off. What it must not
    /// do is pay the full price of the layer it is not drawing.
    ///
    /// <para><b>The bug these pin.</b> With both layers off and two DSOs pinned, the gather walked the
    /// spatial grid exactly as it does with the overlay ON -- and worse than that, because the cheap
    /// magnitude/type gate inside the walk was disabled outright whenever any pin existed (a pin bypasses
    /// those filters, and recognising one used to need the cross-index closure of every object visited).
    /// The caller then dropped everything the walk produced except the two pins. So pinning two targets
    /// with the overlay OFF made panning markedly slower than pinning nothing at all, which is the
    /// opposite of what the toggle implies -- and it is invisible to any measurement taken with an empty
    /// planner, which is how it survived a whole performance investigation.</para>
    ///
    /// <para>Both halves are now fixed: <c>pinnedOnly</c> looks the pins up directly instead of walking,
    /// and the walk closes over the PINS once (membership in a cross-index component is symmetric)
    /// instead of over every object, so the gate runs whether or not anything is pinned.</para>
    /// </summary>
    [Collection("Astrometry")]
    public class SkyMapPinnedOnlyGatherTests(ITestOutputHelper output)
    {
        private static readonly RectF32 Rect = new(0f, 0f, 1600f, 1000f);

        // Two real DSOs a few degrees apart, both inside the test view: the Lagoon (M 8 = NGC 6523) and
        // the Eagle (M 16 = NGC 6611). M 8 is deliberately named by its MESSIER number, which exists
        // only as a cross-index alias of the NGC entry -- so a pin that resolves only by exact index
        // would silently gather nothing and every assertion below would still be satisfiable by the
        // wrong path. It is the case the closure has to handle.
        private const string PinA = "M8";
        private const string PinB = "NGC6611";
        private const double CentreRa = 18.2;
        private const double CentreDec = -20.0;

        private static async Task<CelestialObjectDB> LoadAsync()
        {
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true);
            return db;
        }

        private static IReadOnlySet<CatalogIndex> Pins(params string[] names)
        {
            var set = new HashSet<CatalogIndex>();
            foreach (var name in names)
            {
                CatalogUtils.TryGetCleanedUpCatalogName(name, out var idx).ShouldBeTrue($"'{name}' should parse");
                set.Add(idx);
            }

            return set;
        }

        private static List<OverlayCandidate> Gather(
            CelestialObjectDB db, double fov, IReadOnlySet<CatalogIndex>? pinned, bool pinnedOnly)
        {
            var state = new SkyMapState { CenterRA = CentreRa, CenterDec = CentreDec, FieldOfViewDeg = fov };
            var candidates = new List<OverlayCandidate>(16384);
            OverlayEngine.GatherSkyMapOverlayCandidates(
                state.ComputeViewMatrix(), fov, Rect, 1f, db, pinned, candidates, pinnedOnly);
            return candidates;
        }

        /// <summary>
        /// The load-bearing assertion, and the one that goes red if <c>pinnedOnly</c> is ever ignored:
        /// the gather returns the pins and NOTHING else. A walk that honours the flag returns two
        /// candidates; a walk that does not returns thousands and is then filtered by the caller to the
        /// same visible result, so nothing downstream -- pixels included -- could tell them apart.
        /// </summary>
        [Theory]
        [InlineData(20.0)]
        [InlineData(60.0)]
        [InlineData(120.0)]
        public async Task WithBothLayersOffTheGatherReturnsThePinsAndNothingElse(double fov)
        {
            var db = await LoadAsync();
            var pins = Pins(PinA, PinB);

            var candidates = Gather(db, fov, pins, pinnedOnly: true);

            output.WriteLine($"fov={fov} candidates={candidates.Count}: "
                + string.Join(", ", candidates.Select(c => c.CatalogIndex.ToCanonical())));

            candidates.Count.ShouldBe(2);
            candidates.ShouldAllBe(c => c.IsPinned);
        }

        /// <summary>
        /// Equivalence with the path it replaces: the pinned-only gather must produce the same objects
        /// the full walk produces once the caller's own filter has stripped the layers that are off.
        /// Without this the fast path could be fast and wrong -- a pin resolving to a different catalog
        /// entry, or dropping out because its type or magnitude fails a gate the walk lets it bypass.
        /// </summary>
        [Fact]
        public async Task ThePinnedOnlySetIsWhatTheFullWalkWouldHaveKept()
        {
            var db = await LoadAsync();
            var pins = Pins(PinA, PinB);
            const double Fov = 30.0;

            var fast = Gather(db, Fov, pins, pinnedOnly: true);

            // The walk, filtered exactly as SkyMapTab / VkSkyMapTab filter it with both layers off.
            var walked = Gather(db, Fov, pins, pinnedOnly: false);
            walked.RemoveAll(c => !c.IsPinned);

            var fastKeys = fast.Select(c => (ulong)c.CatalogIndex).OrderBy(k => k).ToArray();
            var walkedKeys = walked.Select(c => (ulong)c.CatalogIndex).OrderBy(k => k).ToArray();

            output.WriteLine($"fast=[{string.Join(", ", fast.Select(c => c.CatalogIndex.ToCanonical()))}] "
                + $"walked=[{string.Join(", ", walked.Select(c => c.CatalogIndex.ToCanonical()))}]");

            fastKeys.ShouldBe(walkedKeys);
            fastKeys.Length.ShouldBe(2);
        }

        /// <summary>
        /// The second half: a pin must not change which OTHER objects the walk admits. It used to, by
        /// switching the magnitude/type gate off for every object visited -- which was only ever a cost
        /// bug, so the way to catch it is to prove the admitted set is identical and let the (now
        /// unconditional) gate stand on that.
        /// </summary>
        [Fact]
        public async Task PinningSomethingDoesNotChangeWhichOtherObjectsTheWalkAdmits()
        {
            var db = await LoadAsync();
            const double Fov = 30.0;

            var withoutPins = Gather(db, Fov, null, pinnedOnly: false);
            var withPins = Gather(db, Fov, Pins(PinA, PinB), pinnedOnly: false);

            var unpinnedKeys = withPins.Where(c => !c.IsPinned).Select(c => (ulong)c.CatalogIndex).ToHashSet();
            var baselineKeys = withoutPins.Select(c => (ulong)c.CatalogIndex).ToHashSet();

            // The pinned entries themselves are the only legitimate difference: they are admitted past
            // the gates by the pin, so they are present in one set and (if faint or an undrawn type)
            // possibly absent from the other.
            var pinnedKeys = withPins.Where(c => c.IsPinned).Select(c => (ulong)c.CatalogIndex).ToHashSet();
            baselineKeys.ExceptWith(pinnedKeys);
            unpinnedKeys.ExceptWith(pinnedKeys);

            output.WriteLine($"baseline={baselineKeys.Count} withPins(unpinned)={unpinnedKeys.Count}");

            unpinnedKeys.Count.ShouldBeGreaterThan(0);
            unpinnedKeys.SetEquals(baselineKeys).ShouldBeTrue(
                $"admitted set changed: +{unpinnedKeys.Except(baselineKeys).Count()} "
                + $"-{baselineKeys.Except(unpinnedKeys).Count()}");
        }

        /// <summary>
        /// A pin recorded under a cross-catalog alias still resolves. M 8 is a Messier number, which the
        /// catalog models as an alias of NGC 6523 rather than as an entry of its own, so this is the
        /// route the closure exists for -- and the failure would be silent: the landmark simply would
        /// not draw.
        /// </summary>
        [Fact]
        public async Task APinRecordedUnderAnAliasStillResolves()
        {
            var db = await LoadAsync();

            var viaMessier = Gather(db, 30.0, Pins(PinA), pinnedOnly: true);
            var viaNgc = Gather(db, 30.0, Pins("NGC6523"), pinnedOnly: true);

            viaMessier.Count.ShouldBe(1);
            viaNgc.Count.ShouldBe(1);
            ((ulong)viaMessier[0].CatalogIndex).ShouldBe((ulong)viaNgc[0].CatalogIndex);
        }

        /// <summary>
        /// Two pins that name the same object produce ONE marker, not two stacked on the same pixel with
        /// two labels fighting for the same slot.
        /// </summary>
        [Fact]
        public async Task TwoAliasesOfTheSameObjectProduceOneCandidate()
        {
            var db = await LoadAsync();

            var candidates = Gather(db, 30.0, Pins(PinA, "NGC6523"), pinnedOnly: true);

            candidates.Count.ShouldBe(1);
        }
    }
}
