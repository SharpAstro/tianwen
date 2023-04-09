using System;

namespace Astap.Lib.Devices;

public readonly struct ExposureSettings
{
    public ExposureSettings(int startX, int startY, int width, int height, byte bin, BitDepth bitDepth)
    {
        StartX = startX;
        StartY = startY;
        Width = width;
        Height = height;
        Bin = bin;
        BitDepth = bitDepth;
    }

    public int Width { get; }
    public int Height { get; }

    public int StartX { get; }
    public int StartY { get; }
    public byte Bin { get; }
    public BitDepth BitDepth { get; }

    public static void WithStartX(ref ExposureSettings @this, int startX)
        => @this = new ExposureSettings(startX, @this.StartY, @this.Width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithStartY(ref ExposureSettings @this, int startY)
        => @this = new ExposureSettings(@this.StartX, startY, @this.Width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithWidth(ref ExposureSettings @this, int width)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, width, @this.Height, @this.Bin, @this.BitDepth);

    public static void WithHeight(ref ExposureSettings @this, int height)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, height, @this.Bin, @this.BitDepth);

    public static void WithBin(ref ExposureSettings @this, byte bin)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, bin, @this.BitDepth);

    public static void WithBitDepth(ref ExposureSettings @this, BitDepth bitDepth)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, @this.Bin, bitDepth);
}
