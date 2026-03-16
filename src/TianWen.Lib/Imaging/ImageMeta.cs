using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Metadata associated with a captured astronomical image.
/// <para>
/// <b>FITS header semantics (OFFSET / PEDESTAL / BLKLEVEL):</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <term>Offset (FITS: OFFSET, BLKLEVEL)</term>
/// <description>Camera register setting — an integer value configured on the camera electronics
/// to bias the ADC output above zero. This is a capture-time setting, not a post-processing value.
/// Community consensus (NINA, SGP, SharpCap): OFFSET and BLKLEVEL both refer to this camera register.
/// Stored in <see cref="Offset"/>; -1 means unknown.</description>
/// </item>
/// <item>
/// <term>Pedestal (FITS: PEDESTAL)</term>
/// <description>ADU value added after calibration to prevent negative pixel values (used by
/// MaximDL, PixInsight). This is stored separately on <see cref="Image"/> as the <c>pedestal</c>
/// constructor parameter, not on <see cref="ImageMeta"/>, because it is a data-level concern
/// rather than a capture-time setting.</description>
/// </item>
/// <item>
/// <term>BZERO</term>
/// <description>FITS data encoding shift (e.g. 32768 for unsigned 16-bit stored as signed).
/// Handled transparently during FITS read/write and is unrelated to either offset or pedestal.</description>
/// </item>
/// </list>
/// </summary>
/// <param name="Instrument">Camera or sensor name (FITS: INSTRUME).</param>
/// <param name="ExposureStartTime">UTC start time of the exposure (FITS: DATE-OBS).</param>
/// <param name="ExposureDuration">Exposure duration (FITS: EXPTIME, EXPOSURE).</param>
/// <param name="FrameType">Frame type: Light, Dark, Flat, Bias, etc. (FITS: IMAGETYP, FRAMETYP).</param>
/// <param name="Telescope">Telescope or optical assembly name (FITS: TELESCOP).</param>
/// <param name="PixelSizeX">Physical pixel width in micrometers (FITS: XPIXSZ).</param>
/// <param name="PixelSizeY">Physical pixel height in micrometers (FITS: YPIXSZ).</param>
/// <param name="FocalLength">Effective focal length in mm (FITS: FOCALLEN). -1 if unknown.</param>
/// <param name="FocusPos">Focuser position in steps (FITS: FOCUSPOS, FOCPOS). -1 if unknown.</param>
/// <param name="Filter">Active filter during capture (FITS: FILTER).</param>
/// <param name="BinX">Horizontal binning factor (FITS: XBINNING).</param>
/// <param name="BinY">Vertical binning factor (FITS: YBINNING).</param>
/// <param name="CCDTemperature">Measured sensor temperature in Celsius (FITS: CCD-TEMP). NaN if unavailable.</param>
/// <param name="SensorType">Sensor type: Monochrome, RGGB, Color, etc. (FITS: BAYERPAT, COLORTYP, CFAIMAGE).</param>
/// <param name="BayerOffsetX">Bayer pattern X offset (FITS: BAYOFFX).</param>
/// <param name="BayerOffsetY">Bayer pattern Y offset (FITS: BAYOFFY).</param>
/// <param name="RowOrder">Pixel row order: TopDown or BottomUp (FITS: ROWORDER).</param>
/// <param name="Latitude">Observatory latitude in decimal degrees (FITS: SITELAT). NaN if unknown.</param>
/// <param name="Longitude">Observatory longitude in decimal degrees (FITS: SITELONG). NaN if unknown.</param>
/// <param name="ObjectName">Target object name, e.g. "M42" (FITS: OBJECT). Empty if unset.</param>
/// <param name="Gain">Camera gain register setting (FITS: GAIN). -1 if unknown.</param>
/// <param name="Offset">Camera offset/black-level register setting (FITS: OFFSET, BLKLEVEL). -1 if unknown.
/// See class-level remarks for OFFSET vs PEDESTAL distinction.</param>
/// <param name="SetCCDTemperature">Requested cooler setpoint in Celsius (FITS: SET-TEMP). NaN if unavailable.</param>
/// <param name="TargetRA">Target right ascension in hours (FITS: OBJCTRA, RA). NaN if unknown.</param>
/// <param name="TargetDec">Target declination in degrees (FITS: OBJCTDEC, DEC). NaN if unknown.</param>
/// <param name="ElectronsPerADU">Electrons per ADU (system gain) (FITS: EGAIN). NaN if unknown.</param>
/// <param name="SWCreator">Software that created the image (FITS: SWCREATE). Empty if unset.</param>
public record struct ImageMeta(
    string Instrument,
    DateTimeOffset ExposureStartTime,
    TimeSpan ExposureDuration,
    FrameType FrameType,
    string Telescope,
    float PixelSizeX,
    float PixelSizeY,
    int FocalLength,
    int FocusPos,
    Filter Filter,
    int BinX,
    int BinY,
    float CCDTemperature,
    SensorType SensorType,
    int BayerOffsetX,
    int BayerOffsetY,
    RowOrder RowOrder,
    float Latitude,
    float Longitude,
    string ObjectName = "",
    short Gain = -1,
    int Offset = -1,
    float SetCCDTemperature = float.NaN,
    double TargetRA = double.NaN,
    double TargetDec = double.NaN,
    float ElectronsPerADU = float.NaN,
    string SWCreator = ""
)
{
    /// <summary>
    /// Pixel scale in arcsec/pixel, derived from pixel size and focal length.
    /// Returns NaN if either value is unavailable.
    /// </summary>
    public readonly double DerivedPixelScale =>
        FocalLength > 0 && PixelSizeX > 0
            ? PixelSizeX / FocalLength * 206.265
            : double.NaN;
}
