using System.Linq;
using System.Xml.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// TianWen.Lib's <c>ILLink.Substitutions.xml</c> lets a trimmed consumer that never opens a catalog
    /// (the Explorer thumbnail DLL) drop the 57 MB of embedded astrometry data by setting the
    /// <c>TianWen.Lib.EmbeddedCatalogs</c> feature switch false. The trimmer matches resource NAMES
    /// exactly and has no wildcard, so a catalog added to the csproj without a line in the XML ships in
    /// every such consumer with nothing saying so. This pins the XML to the built assembly's manifest:
    /// the two must list the same resources, and the gate must be the documented switch.
    /// </summary>
    public class EmbeddedCatalogFeatureSwitchTests
    {
        private const string SubstitutionsResource = "ILLink.Substitutions.xml";
        private const string FeatureSwitch = "TianWen.Lib.EmbeddedCatalogs";

        [Fact]
        public void TheSubstitutionsNameEveryEmbeddedResourceAndNothingElse()
        {
            var assembly = typeof(Image).Assembly;
            using var xml = assembly.GetManifestResourceStream(SubstitutionsResource);
            xml.ShouldNotBeNull($"{SubstitutionsResource} must be embedded in TianWen.Lib");

            var doc = XDocument.Load(xml);
            var listed = doc.Descendants("resource")
                .Select(r => (string?)r.Attribute("name"))
                .Where(n => n is not null)
                .Select(n => n!)
                .ToHashSet();

            var actual = assembly.GetManifestResourceNames()
                .Where(n => n != SubstitutionsResource)
                .ToHashSet();

            // Two directed checks rather than one set equality so the failure names the offender.
            actual.Except(listed).ShouldBeEmpty("embedded in TianWen.Lib but NOT in ILLink.Substitutions.xml (a trimmed consumer with the switch off ships it): ");
            listed.Except(actual).ShouldBeEmpty("listed in ILLink.Substitutions.xml but no longer embedded (a stale line): ");
        }

        [Fact]
        public void EveryRemovalIsGatedOnTheDocumentedSwitchBeingOff()
        {
            using var xml = typeof(Image).Assembly.GetManifestResourceStream(SubstitutionsResource);
            xml.ShouldNotBeNull();

            var doc = XDocument.Load(xml);
            var assemblies = doc.Root.ShouldNotBeNull().Elements("assembly").ToList();
            assemblies.ShouldNotBeEmpty();
            foreach (var element in assemblies)
            {
                ((string?)element.Attribute("fullname")).ShouldBe("TianWen.Lib");
                ((string?)element.Attribute("feature")).ShouldBe(FeatureSwitch);
                ((string?)element.Attribute("featurevalue")).ShouldBe("false");
                element.Elements("resource").ShouldAllBe(r => (string?)r.Attribute("action") == "remove");
            }
        }
    }
}
