using System.Globalization;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Percent labels must read identically on every host.
    /// </summary>
    /// <remarks>
    /// These exist because the obvious <c>{x:P0}</c> does not: the invariant percent pattern puts a
    /// SPACE before the sign while en-US does not, so "Boost 50%" on a Windows dev box was
    /// "Boost 50 %" on a Linux CI runner. It reached a pull request because the only percentages any test
    /// pinned were two split labels, and the box it was written on is the one culture that never varies.
    /// </remarks>
    public class UiFormatTests
    {
        // The cultures are the two that actually disagreed, plus one that writes the sign FIRST -- if a
        // future edit reaches for a culture-aware pattern again, tr-TR is the one that shows it loudest.
        [Theory]
        [InlineData("")]        // invariant: "50 %" under P0, separated by U+0020 (measured on Ubuntu 24.04)
        [InlineData("en-US")]   // "50%" under P0 -- the one that hid the bug
        [InlineData("de-DE")]   // "50 %"
        [InlineData("tr-TR")]   // "%50" -- sign leads
        public void APercentageReadsTheSameInEveryCulture(string culture)
        {
            CultureInfo requested;
            try
            {
                requested = CultureInfo.GetCultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                // Invariant-globalization mode collapses every culture and refuses to construct a named
                // one. That is a real configuration here -- TianWen.UI.Web sets InvariantGlobalization --
                // so a run under it must SKIP these cases rather than fail them: the thing under test is
                // the formatting, and it is covered by the invariant case that always constructs.
                Assert.Skip($"culture '{culture}' is unavailable (invariant-globalization mode)");
                return;
            }

            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = requested;

                UiFormat.Percent0(0.5f).ShouldBe("50%");
                UiFormat.Percent0(1.5f).ShouldBe("150%");
                UiFormat.Percent0(0f).ShouldBe("0%");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        /// <summary>
        /// No space of any kind before the sign. The one actually observed was U+0020 on Ubuntu 24.04,
        /// but fr-FR uses U+00A0 and some locales U+202F -- all invisible in a diff and most terminals,
        /// so each is rejected by name rather than trusting the one that happened to be seen.
        /// </summary>
        [Fact]
        public void TheSignHangsDirectlyOnTheDigits()
        {
            var formatted = UiFormat.Percent0(0.42f);

            formatted.ShouldBe("42%");
            formatted.ShouldNotContain(" ");
            formatted.ShouldNotContain("\u00A0");
            formatted.ShouldNotContain("\u202F");
        }

        /// <summary>
        /// Rounds to whole percent rather than truncating, so 0.999 does not report 99%.
        /// </summary>
        /// <remarks>
        /// The exact half is deliberately not a case here. <c>0.005f</c> is not representable -- it is
        /// really 0.00499999989, so it scales to 0.4999999 and rounds DOWN, which is correct arithmetic
        /// on the value that actually exists rather than the one that was written. Asserting "1%" for it
        /// (as a first draft of this did) tests the literal a reader imagines instead of the float.
        /// </remarks>
        [Theory]
        [InlineData(0.999f, "100%")]
        [InlineData(0.006f, "1%")]
        [InlineData(0.004f, "0%")]
        public void AFractionRoundsToTheNearestWholePercent(float fraction, string expected)
            => UiFormat.Percent0(fraction).ShouldBe(expected);
    }
}
