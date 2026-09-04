using Shouldly;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// The coverage report's TSV escaping runs on free text straight out of FITS headers, and the
    /// interesting input is the one a CURATED archive never contains: a card the capture program
    /// never wrote at all. Pointing the report at an un-ingested pile threw a
    /// <see cref="System.NullReferenceException"/> on the first frame with no OBJECT, after scanning
    /// 11,487 headers and 28 sessions -- the whole run lost to one absent card.
    /// </summary>
    [Collection("Imaging")]
    public class CalibrationCoverageCleanTests
    {
        [Fact]
        public void AnAbsentHeaderCardBecomesAnEmptyFieldRatherThanThrowing()
            => CalibrationCoverageReport.Clean(null).ShouldBe("");

        [Theory]
        [InlineData("Rim Nebula", "Rim Nebula")]
        [InlineData("", "")]
        [InlineData("Vela\tSNR", "Vela SNR")]
        [InlineData("Eta\r\nCar", "Eta  Car")]
        [InlineData("a\tb\rc\nd", "a b c d")]
        public void SeparatorsAndLineBreaksBecomeSpacesSoOneFieldStaysOneField(string input, string expected)
            => CalibrationCoverageReport.Clean(input).ShouldBe(expected);
    }
}
