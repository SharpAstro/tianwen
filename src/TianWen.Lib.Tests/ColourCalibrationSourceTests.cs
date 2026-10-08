using System.Text.Json;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="ColourCalibration.Source"/> is an enum (#1356), so a calibration's JSON never ends in a null, and it still
/// reads and writes as the card's string did: in the FITS card and in the image metadata's JSON, which a frame carries
/// across the node's wire with enum names.
/// </summary>
public class ColourCalibrationSourceTests
{
    [Theory]
    [InlineData(ColourCalibrationSource.Spcc, "SPCC")]
    [InlineData(ColourCalibrationSource.SkyBackground, "SKYBG")]
    public void TheSourceIsTheCardsStringInTheCardAndInTheJson(ColourCalibrationSource source, string card)
    {
        ColourCalibration.CardOf(source).ShouldBe(card);
        ColourCalibration.FromCard(card).ShouldBe(source);

        var meta = new ImageMeta { ColourCalibration = new ColourCalibration(1.52f, 1f, 1.78f, source) };
        var json = JsonSerializer.Serialize(meta, ImageJsonSerializerContext.Default.ImageMeta);
        json.ShouldContain($"\"source\": \"{card}\"");
        JsonSerializer.Deserialize(json, ImageJsonSerializerContext.Default.ImageMeta).ColourCalibration.ShouldBe(meta.ColourCalibration);
    }

    [Fact]
    public void AnAbsentOrForeignCardReadsAsSpccAsItAlwaysHas()
    {
        ColourCalibration.FromCard(null).ShouldBe(ColourCalibrationSource.Spcc);
        ColourCalibration.FromCard("  skybg ").ShouldBe(ColourCalibrationSource.SkyBackground);
        ColourCalibration.FromCard("GRAYWORLD").ShouldBe(ColourCalibrationSource.Spcc);
    }
}
