using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The compressed Tycho-2 catalogue, for the tests that are ABOUT the compressed form.
    /// </summary>
    /// <remarks>
    /// <para>TianWen.Lib ships <c>tyc2.bin</c> EXPANDED, so a region query reads its own bytes off the
    /// mapped assembly image instead of decompressing 43.5 MB to reach 59 KB. Shipping the <c>.lz</c>
    /// as well would put two copies of one catalogue in the library for no runtime gain.</para>
    /// <para>But the <c>.lz</c> is still real and still matters: it is the committed artifact, it is
    /// what <c>pages.yml</c> stages into the web app's static assets (whole-catalogue fallback plus 166
    /// region-aligned members), and its MEMBER structure is the subject of
    /// <c>Tycho2MemberManifest</c> / <c>Tycho2RegionSelector</c> / <c>Tycho2PartialCatalog</c> and the
    /// <c>TryLoadTycho2BulkFromCompressed</c> injection seam the WASM build loads through. Those tests
    /// need the compressed bytes by definition -- a decompressed catalogue cannot exercise
    /// member-boundary logic.</para>
    /// <para>So the test project embeds it instead, under the SAME logical name. The tests get their
    /// input, the shipped library stays one copy, and the fixture lives with the tests that need it
    /// rather than in the product.</para>
    /// </remarks>
    internal static class Tycho2TestCatalog
    {
        /// <summary>
        /// Whichever assembly carries a catalogue resource: the test project first, then the library.
        /// </summary>
        /// <remarks>
        /// Returning the ASSEMBLY rather than the bytes is what keeps the change at each call site to
        /// one expression -- every one of them already does its own
        /// <c>GetManifestResourceNames().FirstOrDefault(EndsWith(...))</c> and then opens the stream,
        /// and those lines carry per-test detail worth leaving alone. It also covers the mixed case
        /// honestly: <c>tyc2_gsc_bounds.bin.lz</c> is still in the LIBRARY (92 KB, and the runtime
        /// spatial index is built from it), while <c>tyc2.bin.lz</c> now lives with the tests, so a
        /// helper hard-coded to one assembly would be wrong for one of them.
        /// </remarks>
        internal static Assembly AssemblyWith(string resourceSuffix)
        {
            foreach (var asm in new[] { typeof(Tycho2TestCatalog).Assembly, typeof(Lib.Astrometry.Catalogs.ICelestialObjectDB).Assembly })
            {
                if (asm.GetManifestResourceNames().Any(n => n.EndsWith(resourceSuffix, StringComparison.Ordinal)))
                {
                    return asm;
                }
            }

            // Neither has it (a lightweight build). Hand back the library so the caller's own
            // "must be embedded" assertion is what reports it, rather than a null-reference here.
            return typeof(Lib.Astrometry.Catalogs.ICelestialObjectDB).Assembly;
        }
    }
}
