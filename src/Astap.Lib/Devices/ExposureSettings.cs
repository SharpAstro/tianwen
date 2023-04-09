using System;

namespace Astap.Lib.Devices;

public readonly struct ExposureSettings
{
    public ExposureSettings(Index startX, Index startY, Index width, Index height, byte bin, BitDepth bitDepth)
    {
        StartX = startX.Value;
        StartY = startY.Value;
        Width = width.Value;
        Height = height.Value;
        Bin = bin;
        BitDepth = bitDepth;
    }

    public int Width { get; }
    public int Height { get; }

    public int StartX { get; }
    public int StartY { get; }
    public byte Bin { get; }
    public BitDepth BitDepth { get; }

    public static void WithStartX(ref ExposureSettings @this, Index startX)
        => @this = new ExposureSettings(startX, @this.StartY, @this.Width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithStartY(ref ExposureSettings @this, Index startY)
        => @this = new ExposureSettings(@this.StartX, startY, @this.Width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithWidth(ref ExposureSettings @this, Index width)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithHeight(ref ExposureSettings @this, Index height)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, height, @this.Bin, @this.BitDepth);

    public static void WithBin(ref ExposureSettings @this, byte bin)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, bin, @this.BitDepth);

    public static void WithBitDepth(ref ExposureSettings @this, BitDepth bitDepth)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, @this.Bin, bitDepth);
}
