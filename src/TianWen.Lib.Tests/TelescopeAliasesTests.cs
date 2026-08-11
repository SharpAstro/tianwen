using Shouldly;
using System.Linq;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for <see cref="TelescopeAliases"/> and the <see cref="CalibrationResolver.CalTrain"/>
    /// label round-trip it stands on. One lens recorded under two TELESCOP spellings must report as
    /// one optical train, without ever collapsing two genuinely different light paths.
    /// </summary>
    public class TelescopeAliasesTests
    {
        private static readonly float[] OneStar = [3.0f];

        /// <summary>Describe and TryParseDescription are one grammar written twice, so the round-trip
        /// is the invariant that keeps them honest. Covers every shape Describe can emit: full train,
        /// no focal length, no telescope, bare camera, and the unknown-camera placeholder.</summary>
        [Theory]
        [InlineData("ZWO ASI533MC Pro", "Samyang 135 f/2 ED", 130)]
        [InlineData("ZWO ASI585MC Pro", "WO ZS61", 360)]
        [InlineData("ZWO ASI533MC Pro", "Samyang 135 f/2 ED", -1)]
        [InlineData("ZWO ASI533MC Pro", "", 130)]
        [InlineData("ZWO ASI533MC Pro", "", -1)]
        [InlineData("", "", -1)]
        [InlineData("", "Newtonian", 800)]
        public void Describe_RoundTripsThroughTryParseDescription(string camera, string telescope, int focalLength)
        {
            var original = new CalibrationResolver.CalTrain(camera, telescope, focalLength);

            CalibrationResolver.CalTrain.TryParseDescription(original.Describe(), out var parsed).ShouldBeTrue();

            parsed.Instrument.ShouldBe(camera);
            parsed.Telescope.ShouldBe(telescope);
            parsed.FocalLength.ShouldBe(focalLength);
            parsed.Describe().ShouldBe(original.Describe());
        }

        [Fact]
        public void TryParseDescription_OnBlank_IsFalseAndYieldsNoNullFields()
        {
            // CalTrain is a record struct, so a careless `default` would hand back null strings and
            // NRE in the caller's string comparisons.
            CalibrationResolver.CalTrain.TryParseDescription("   ", out var train).ShouldBeFalse();

            train.Instrument.ShouldBe("");
            train.Telescope.ShouldBe("");
            train.FocalLength.ShouldBe(-1);
        }

        [Fact]
        public void CanonicalizeLabel_RewritesOnlyTheTelescopeSegment()
        {
            TelescopeAliases.CanonicalizeLabel("ZWO ASI533MC Pro / SAMYANG 135mm @ 130mm")
                .ShouldBe("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm");
        }

        [Theory]
        [InlineData("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm")] // already canonical
        [InlineData("SVBONY SV605CC / SH61 EDPH @ 270mm")]            // no alias applies
        [InlineData("not a train label at all")]                      // unparseable-ish: passes through
        [InlineData("")]
        public void CanonicalizeLabel_LeavesAnythingWithoutAnAliasAlone(string label)
        {
            TelescopeAliases.CanonicalizeLabel(label).ShouldBe(label);
        }

        [Fact]
        public void Accumulator_MergesTheSameLensRecordedUnderTwoSpellings()
        {
            // The real archive split one Samyang 135 into a 3-session train and a 35-session train
            // purely on header spelling, weakening both field-radius profiles.
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(Psf("a", "ZWO ASI533MC Pro / SAMYANG 135mm @ 130mm"));
            acc.Add(Psf("b", "ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm"));

            var report = acc.Build();

            var train = report.Trains.ShouldHaveSingleItem();
            train.OpticalTrain.ShouldBe("ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm");
            train.Sessions.ShouldBe(2);
            // The merge is disclosed, so a reader can tell a real 2-session train from an alias.
            train.RecordedAs.ShouldBe([
                "ZWO ASI533MC Pro / SAMYANG 135mm @ 130mm",
                "ZWO ASI533MC Pro / Samyang 135 f/2 ED @ 130mm"], ignoreOrder: true);
        }

        [Fact]
        public void Accumulator_KeepsOneScopeSplitByItsCorrectorApart()
        {
            // WO ZS61 at 288mm is the 0.8x reducer, at 360mm the flattener-only path (360 x 0.8 =
            // 288). Same glass, different light path: a reducer changes the off-axis aberration the
            // field-radius profile exists to measure, so these must NEVER merge. This is the guard
            // against an alias table that starts collapsing on name alone.
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(Psf("reduced", "ZWO ASI585MC Pro / WO ZS61 @ 288mm"));
            acc.Add(Psf("flattened", "ZWO ASI585MC Pro / WO ZS61 @ 360mm"));

            var report = acc.Build();

            report.Trains.Length.ShouldBe(2);
            report.Trains.Select(t => t.OpticalTrain).ShouldBe([
                "ZWO ASI585MC Pro / WO ZS61 @ 288mm",
                "ZWO ASI585MC Pro / WO ZS61 @ 360mm"], ignoreOrder: true);
        }

        [Fact]
        public void Accumulator_OnAnUnmergedTrain_RecordsTheSingleSpelling()
        {
            var acc = new DatasetPsfNoiseReport.Accumulator(radiusBins: 1);
            acc.Add(Psf("a", "SVBONY SV605CC / SH61 EDPH @ 270mm"));
            acc.Add(Psf("b", "SVBONY SV605CC / SH61 EDPH @ 270mm"));

            var train = acc.Build().Trains.ShouldHaveSingleItem();

            train.Sessions.ShouldBe(2);
            train.RecordedAs.ShouldHaveSingleItem().ShouldBe("SVBONY SV605CC / SH61 EDPH @ 270mm");
        }

        private static DatasetPsfNoiseReport.SessionPsf Psf(string sessionId, string opticalTrain) => new(
            SessionId: sessionId,
            OpticalTrain: opticalTrain,
            SubFwhm: OneStar,
            SubHfd: OneStar,
            SubEllipticity: OneStar,
            MasterNoiseRelative: 0.001,
            Bins: [new DatasetPsfNoiseReport.RadiusSamples(OneStar, OneStar)]);
    }
}
