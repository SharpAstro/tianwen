using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Tycho-2 ships expanded, so a region query can read its own bytes instead of decompressing the
    /// catalogue.
    /// </summary>
    /// <remarks>
    /// <para><c>tyc2.bin</c> was always the format the runtime reads -- a stream count, a per-GSC-region
    /// offset table, then region-major 17-byte records read straight out of a flat span. lzip was only
    /// ever the container, and its members must be decoded WHOLE, so reaching one region's ~59 KB cost
    /// decompressing all 43.5 MB. At 1.45x compression that container was buying little and costing the
    /// largest single line item in a cold solve.</para>
    /// <para>The committed artifact is still the <c>.lz</c>: the repository has no LFS budget to spend
    /// and <c>.gitattributes</c> routes a bare <c>tyc2.bin</c> there too, so the expansion happens at
    /// BUILD time into <c>obj/</c> (the <c>ExpandTycho2</c> target, short-circuited by MSBuild's
    /// Inputs/Outputs once the output is newer than the input). Nothing about the repo, CI or the
    /// pages.yml web staging changes.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class Tycho2MappableResourceTests(ITestOutputHelper output)
    {
        private static string[] Tycho2Resources() =>
            [.. typeof(CelestialObjectDB).Assembly.GetManifestResourceNames()
                .Where(n => n.Contains("tyc2", StringComparison.OrdinalIgnoreCase))
                .Order()];

        [Fact]
        public void TheCatalogShipsExpandedAndTheCompressedCopyIsNotAlsoEmbedded()
        {
            var assembly = typeof(CelestialObjectDB).Assembly;
            foreach (var n in Tycho2Resources())
            {
                using var s = assembly.GetManifestResourceStream(n);
                output.WriteLine($"  {n,-56} {s?.Length ?? -1,12:N0} bytes");
            }

            var names = Tycho2Resources();
            names.ShouldContain(n => n.EndsWith(".tyc2.bin"), "the expanded catalogue must be embedded");

            // Both would be ~74 MB of assembly for one catalogue. The .lz stays in the source tree as
            // the committed artifact and the web's static-asset source, but must not also ship.
            names.ShouldNotContain(n => n.EndsWith(".tyc2.bin.lz"),
                "the compressed copy must not ship alongside the expanded one");
        }

        [Fact]
        public void TheExpandedCatalogIsSeekableSoARegionCanBeReadWithoutTheRest()
        {
            var assembly = typeof(CelestialObjectDB).Assembly;
            var name = Tycho2Resources().FirstOrDefault(n => n.EndsWith(".tyc2.bin"));
            name.ShouldNotBeNull();

            using var stream = assembly.GetManifestResourceStream(name);
            stream.ShouldNotBeNull();

            // The whole point: an embedded resource is an UnmanagedMemoryStream over the mapped image,
            // so this lands on an arbitrary offset without the preceding bytes ever being touched.
            stream.CanSeek.ShouldBeTrue();
            (stream is UnmanagedMemoryStream).ShouldBeTrue("must be mapped, not materialised, or partial reads save nothing");

            var midpoint = stream.Length / 2;
            stream.Seek(midpoint, SeekOrigin.Begin);
            Span<byte> probe = stackalloc byte[16];
            stream.ReadExactly(probe);
            output.WriteLine($"{name}: {stream.Length:N0} bytes, seek to {midpoint:N0} read {Convert.ToHexString(probe)}");
        }

        /// <summary>
        /// The catalogue still decodes to the same stars, which a container change must not move.
        /// </summary>
        [Fact]
        public async Task TheExpandedCatalogDecodesToTheSameStars()
        {
            var ct = TestContext.Current.CancellationToken;
            var db = await SharedCatalogDB.InitAsync(ct);

            db.Tycho2StarCount.ShouldBe(2557501, "the expanded catalogue must hold exactly the stars the compressed one did");

            var stars = new Tycho2StarLite[8];
            db.CopyTycho2Stars(stars).ShouldBe(stars.Length);
            foreach (var s in stars)
            {
                output.WriteLine($"  RA={s.RaHours:F6}h Dec={s.DecDeg:F6} V={s.VMag:F3} B-V={s.BMinusV:F3}");
                double.IsNaN(s.RaHours).ShouldBeFalse();
                s.RaHours.ShouldBeInRange(0, 24);
                s.DecDeg.ShouldBeInRange(-90, 90);
            }
        }

        /// <summary>
        /// What the container change actually bought, on a DB that has never loaded it.
        /// </summary>
        /// <remarks>
        /// Reports rather than asserts: a wall-clock threshold in a test is a flake on a busy box, and
        /// the number that matters (a cold <c>tianwen solve</c>) is a whole-process measurement this
        /// cannot make. <c>SharedCatalogDB</c> is deliberately not used -- it is process-cached, so it
        /// would time a no-op.
        /// </remarks>
        [Fact]
        public async Task ReportTycho2LoadCost()
        {
            var ct = TestContext.Current.CancellationToken;
            var db = new CelestialObjectDB();

            var sw = Stopwatch.StartNew();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            sw.Stop();

            output.WriteLine($"fresh InitDBAsync(waitForTycho2BulkLoad: true): {sw.Elapsed.TotalMilliseconds:F0} ms");
            output.WriteLine($"Tycho-2 stars: {db.Tycho2StarCount:N0}");

            // The per-phase breakdown is the point. Tycho-2 loads on a BACKGROUND task overlapped with
            // the other phases, so the saving visible in the total is only the part that stuck out past
            // them -- the "tycho2-join" phase, which is the idle wait. Reading the total alone
            // understates what the container change did to the load itself and overstates what any
            // further work on it can win.
            foreach (var (phase, elapsed) in db.LastInitPhaseTimings)
            {
                output.WriteLine($"  {phase,-34} {elapsed.TotalMilliseconds,7:F1} ms");
            }

            db.Tycho2StarCount.ShouldBeGreaterThan(0);
        }
    }
}
