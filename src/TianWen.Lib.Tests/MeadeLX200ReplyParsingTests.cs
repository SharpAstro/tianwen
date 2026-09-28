using System;
using System.Globalization;
using System.Text;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The LX200 base's reply parsers, fed the bytes a mount answers (#837).
/// </summary>
public class MeadeLX200ReplyParsingTests
{
    // :Gg# is degrees WEST; a mount may answer the signed form (east negative) or 0 to 360.
    [Theory]
    [InlineData("-016*18", 16.3d)]
    [InlineData("-016ß18", 16.3d)]
    [InlineData("100*30", -100.5d)]
    [InlineData("+100*30", -100.5d)]
    [InlineData("343*42", 16.3d)]
    [InlineData("214*00", 146d)]
    [InlineData("180*00", 180d)]
    [InlineData("000*00", 0d)]
    public void AGgReplyReadsBackAsAnEastPositiveLongitude(string reply, double expectedEast)
    {
        MeadeLX200ProtocolMountDriverBase<FakeDevice>.TryParseLatOrLong(Encoding.Latin1.GetBytes(reply), out var west).ShouldBeTrue();

        MeadeLX200ProtocolMountDriverBase<FakeDevice>.EastLongitudeFromWest(west).ShouldBe(expectedEast, 1e-9);
    }

    [Theory]
    [InlineData("+48*12", 48.2d)]
    [InlineData("48ß12", 48.2d)]
    [InlineData("-37*54", -37.9d)]
    public void AGtReplyReadsBackAsTheLatitude(string reply, double expected)
    {
        MeadeLX200ProtocolMountDriverBase<FakeDevice>.TryParseLatOrLong(Encoding.Latin1.GetBytes(reply), out var latitude).ShouldBeTrue();

        latitude.ShouldBe(expected, 1e-9);
    }

    // :GG# decimal hours, parsed the same whatever the machine's culture.
    [Theory]
    [InlineData("-05.5", -5.5d)]
    [InlineData("+09.5", 9.5d)]
    [InlineData("+10", 10d)]
    [InlineData("-08", -8d)]
    public void AGGReplyParsesTheSameInEveryCulture(string reply, double expectedHours)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

                MeadeLX200ProtocolMountDriverBase<FakeDevice>.TryParseUtcOffset(reply, out var offset).ShouldBeTrue(culture);
                offset.ShouldBe(TimeSpan.FromHours(expectedHours), culture);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
