using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// TianWen.Lib's <c>ILLink.Substitutions.xml</c> lets a trimmed consumer drop the embedded data it never
    /// reads, one <c>TianWen.Lib.EmbeddedData.&lt;group&gt;</c> feature switch a group (#2155): the Explorer
    /// thumbnail DLL reads none of the 57 MB, the browser app no imaging data and no simulated device's. The
    /// trimmer matches resource NAMES exactly and has no wildcard, so a catalog added to the csproj without a
    /// line in the XML ships in every such consumer with nothing saying so. This pins the XML to the built
    /// assembly's manifest: every embedded resource is in exactly one group, each group is gated on a
    /// documented switch, and the consumer that reads nothing names them all.
    /// </summary>
    public class EmbeddedCatalogFeatureSwitchTests
    {
        private const string SubstitutionsResource = "ILLink.Substitutions.xml";
        private const string SwitchPrefix = "TianWen.Lib.EmbeddedData.";

        private static readonly string[] Groups = ["Sky", "Stars", "Imaging", "FakeDevices"];

        private static XDocument Substitutions()
        {
            using var xml = typeof(Image).Assembly.GetManifestResourceStream(SubstitutionsResource);
            xml.ShouldNotBeNull($"{SubstitutionsResource} must be embedded in TianWen.Lib");
            return XDocument.Load(xml);
        }

        [Fact]
        public void TheSubstitutionsNameEveryEmbeddedResourceOnceAndNothingElse()
        {
            var assembly = typeof(Image).Assembly;
            var names = Substitutions().Descendants("resource")
                .Select(r => (string?)r.Attribute("name"))
                .OfType<string>()
                .ToList();
            var listed = names.ToHashSet();

            var actual = assembly.GetManifestResourceNames()
                .Where(n => n != SubstitutionsResource)
                .ToHashSet();

            // Two directed checks rather than one set equality so the failure names the offender.
            actual.Except(listed).ShouldBeEmpty("embedded in TianWen.Lib but NOT in ILLink.Substitutions.xml (a trimmed consumer with the switch off ships it): ");
            listed.Except(actual).ShouldBeEmpty("listed in ILLink.Substitutions.xml but no longer embedded (a stale line): ");
            names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)
                .ShouldBeEmpty("listed in more than one group, so which switch removes it depends on who reads the file: ");
        }

        [Fact]
        public void EveryRemovalIsGatedOnOneOfTheDocumentedSwitchesBeingOff()
        {
            var assemblies = Substitutions().Root.ShouldNotBeNull().Elements("assembly").ToList();
            assemblies.Select(a => (string?)a.Attribute("feature"))
                .ShouldBe(Groups.Select(g => SwitchPrefix + g), ignoreOrder: true);
            foreach (var element in assemblies)
            {
                ((string?)element.Attribute("fullname")).ShouldBe("TianWen.Lib");
                ((string?)element.Attribute("featurevalue")).ShouldBe("false");
                element.Elements("resource").ShouldNotBeEmpty();
                element.Elements("resource").ShouldAllBe(r => (string?)r.Attribute("action") == "remove");
            }
        }

        [Theory]
        [InlineData("Sky", "NGC.gs.gz")]
        [InlineData("Stars", "tyc2_gsc_bounds.bin.lz")]
        [InlineData("Stars", "hd_hip_cross.bin.gz")]
        [InlineData("Imaging", "pickles_sed.gs.gz")]
        [InlineData("FakeDevices", "jupiter.rgb.gz")]
        public void AFileIsInTheGroupItsReaderBelongsTo(string group, string file)
        {
            var element = Substitutions().Root.ShouldNotBeNull().Elements("assembly")
                .Single(a => (string?)a.Attribute("feature") == SwitchPrefix + group);
            element.Elements("resource").Select(r => (string?)r.Attribute("name"))
                .ShouldContain(n => n != null && n.EndsWith("." + file, StringComparison.Ordinal));
        }

        /// <summary>
        /// There is no switch for "all of it": the thumbnail provider reads none, so its project names every
        /// group, and a group added later that it does not name would put that data back into the shell DLL.
        /// </summary>
        [Fact]
        public void TheThumbnailProviderSwitchesOffEveryGroup()
        {
            if (FindSourceRoot() is not { } root)
            {
                return;
            }

            var project = XDocument.Load(Path.Combine(root, "TianWen.Shell.Thumbnails", "TianWen.Shell.Thumbnails.csproj"));
            var off = project.Descendants("RuntimeHostConfigurationOption")
                .Where(o => (string?)o.Attribute("Value") == "false" && (string?)o.Attribute("Trim") == "true")
                .Select(o => (string?)o.Attribute("Include"))
                .ToList();

            var switches = Substitutions().Root.ShouldNotBeNull().Elements("assembly").Select(a => (string?)a.Attribute("feature"));
            foreach (var feature in switches)
            {
                off.ShouldContain(feature);
            }
        }

        /// <summary>
        /// Walks up from the test binary to the repository's <c>src</c>. Null when the sources are not beside the binary (a packaged
        /// run), and the test then skips: not seeing the source is not a regression.
        /// </summary>
        private static string? FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src");
                if (File.Exists(Path.Combine(candidate, "TianWen.slnx")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
