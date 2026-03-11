namespace TianWen.Lib.Imaging;

public record struct StretchParameters(double Factor, double ShadowsClipping)
{
    public static readonly StretchParameters Default = new(0.1, -5.0);

    public static readonly StretchParameters[] Presets =
    [
        new(0.1, -5.0),
        new(0.1, -3.0),
        new(0.15, -5.0),
        new(0.15, -3.0),
        new(0.2, -5.0),
        new(0.2, -3.0),
        new(0.25, -5.0),
        new(0.25, -3.0),
    ];

    public override readonly string ToString() => $"({Factor}, {-ShadowsClipping})";
}
