using Shouldly;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="OpticalSystems"/>: the coarse refractor-vs-Newtonian-vs-camera-lens
    /// classification the PSF report annotates each (train, filter) section with. The interesting
    /// edges are the two absences: no TELESCOP at all is a bare-lens rig by construction, while an
    /// unknown NAME must stay unclassified rather than be guessed at.
    /// </summary>
    public class OpticalSystemsTests
    {
        [Theory]
        [InlineData("SH61 EDPH", OpticalSystem.Refractor)]
        [InlineData("WO ZS61", OpticalSystem.Refractor)]
        [InlineData("WO RC51", OpticalSystem.Refractor)]
        [InlineData("Samyang 135 f/2 ED", OpticalSystem.CameraLens)]
        // The raw header spelling classifies via TelescopeAliases, so a classification never has to
        // be entered twice for one physical lens.
        [InlineData("SAMYANG 135mm", OpticalSystem.CameraLens)]
        // No TELESCOP = a camera behind a bare photographic lens; that IS the classification.
        [InlineData("", OpticalSystem.CameraLens)]
        [InlineData("   ", OpticalSystem.CameraLens)]
        // Not in the archive (checked across every store 2026-08-17); stays unclassified until a
        // bake actually surfaces it and someone adds the reviewed table entry.
        [InlineData("SW8", OpticalSystem.Unclassified)]
        public void Classify_KnowsTheArchiveAndRefusesToGuess(string telescope, OpticalSystem expected)
            => OpticalSystems.Classify(telescope).ShouldBe(expected);

        [Theory]
        [InlineData("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm", OpticalSystem.CameraLens)]
        [InlineData("SVBONY SV605CC / SH61 EDPH @ 270mm", OpticalSystem.Refractor)]
        // A bare-lens label has no telescope slot at all, which parses to an empty telescope.
        [InlineData("ZWO ASI585MC Pro @ 24mm", OpticalSystem.CameraLens)]
        // An unparseable label is NOT a camera-lens claim: we could not read the telescope slot.
        [InlineData("", OpticalSystem.Unclassified)]
        public void ClassifyLabel_ReadsTheTelescopeSlotOfATrainLabel(string label, OpticalSystem expected)
            => OpticalSystems.ClassifyLabel(label).ShouldBe(expected);

        [Theory]
        [InlineData(OpticalSystem.Refractor, "refractor")]
        [InlineData(OpticalSystem.CameraLens, "camera lens")]
        [InlineData(OpticalSystem.Newtonian, "Newtonian")]
        [InlineData(OpticalSystem.Unclassified, "(unclassified)")]
        public void Label_IsTheWordsTheReportPrints(OpticalSystem kind, string expected)
            => kind.Label.ShouldBe(expected);
    }
}
