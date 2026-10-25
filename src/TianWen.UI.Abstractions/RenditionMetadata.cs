using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The text "Save image as displayed" writes into a PNG: every display setting the picture was rendered with, as JSON under
/// <see cref="RenditionKeyword"/>, beside the program and the source file. Where <see cref="RenditionFileName"/> names only
/// what moved off its default, this records all of it, so the file says how it was made even when its name was changed.
/// </summary>
/// <remarks>A note for the person who saved it, as the name is: nothing reads it back.</remarks>
public static class RenditionMetadata
{
    /// <summary>The PNG keyword the settings are written under.</summary>
    public const string RenditionKeyword = "TianWen:Rendition";

    /// <summary>
    /// The settings as JSON, written by hand through <see cref="Utf8JsonWriter"/> so it needs no serialiser context (AOT).
    /// </summary>
    /// <param name="state">The viewer's display settings.</param>
    /// <param name="shownMode">The stretch actually on show, <see cref="StretchMode.Auto"/> resolved.</param>
    /// <param name="enhanced">Whether the document is an enhance's result.</param>
    /// <param name="starSplitShown">Whether a star split is composed into the picture; the star settings are written only then.</param>
    public static string Json(ViewerState state, StretchMode shownMode, bool enhanced, bool starSplitShown)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("enhanced", enhanced);
            w.WriteString("stretch", shownMode.ToString());
            if (state.StretchMode != shownMode)
            {
                w.WriteString("stretchChosen", state.StretchMode.ToString());
            }
            w.WriteNumber("stretchFactor", state.StretchParameters.Factor);
            w.WriteNumber("shadowsClipping", state.StretchParameters.ShadowsClipping);
            w.WriteString("channel", state.ChannelView.ToString());
            w.WriteString("debayer", state.DebayerAlgorithm.ToString());
            w.WriteBoolean("colourCalibration", state.ColorCalibrationEnabled);

            var (r, g, b) = state.ManualWhiteBalance;
            w.WriteStartArray("whiteBalance");
            w.WriteNumberValue(r);
            w.WriteNumberValue(g);
            w.WriteNumberValue(b);
            w.WriteEndArray();

            w.WriteBoolean("backgroundNeutralization", state.BackgroundNeutralizationEnabled);
            w.WriteString("backgroundNeutralizationMethod", state.BackgroundNeutralizationMethod.ToString());
            w.WriteNumber("backgroundNeutralizationStrength", state.BackgroundNeutralizationStrength);
            w.WriteBoolean("backgroundReduce", state.BackgroundReduceEnabled);
            w.WriteNumber("backgroundSkyLevel", state.BackgroundSkyLevel);

            w.WriteNumber("curvesBoost", state.CurvesBoost);
            w.WriteNumber("curvesMode", state.CurvesMode);
            WriteFloats(w, "curve", state.CurveData);
            w.WriteNumber("hdrAmount", state.HdrAmount);
            w.WriteNumber("hdrKnee", state.HdrKnee);
            w.WriteNumber("finishContrast", state.FinishContrast);
            w.WriteNumber("finishSaturation", state.FinishSaturation);

            if (starSplitShown)
            {
                w.WriteStartObject("stars");
                w.WriteString("method", state.StarMethod.ToString());
                w.WriteNumber("stretch", state.StarStretch);
                w.WriteNumber("amount", state.StarAmount);
                w.WriteNumber("saturation", state.StarSaturation);
                w.WriteEndObject();
            }

            w.WriteBoolean("waveletSharpen", state.WaveletSharpenEnabled);
            if (state.WaveletSharpenEnabled)
            {
                WriteFloats(w, "waveletGains", state.WaveletGains);
            }

            if (state.DisplayCrop is { } crop)
            {
                w.WriteStartArray("crop");
                w.WriteNumberValue(crop.X);
                w.WriteNumberValue(crop.Y);
                w.WriteNumberValue(crop.Width);
                w.WriteNumberValue(crop.Height);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// What the program that wrote the file calls itself, for the PNG <c>Software</c> keyword.
    /// </summary>
    public static string Software
        => "TianWen " + (typeof(Image).Assembly.GetName().Version?.ToString(3) ?? "").TrimEnd();

    private static void WriteFloats(Utf8JsonWriter w, string name, ImmutableArray<float> values)
    {
        w.WriteStartArray(name);
        if (!values.IsDefault)
        {
            foreach (var v in values)
            {
                w.WriteNumberValue(v);
            }
        }
        w.WriteEndArray();
    }
}
