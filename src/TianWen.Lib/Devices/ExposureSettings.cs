using System;

namespace TianWen.Lib.Devices;

public readonly struct ExposureSettings
{
    public ExposureSettings(int startX, int startY, int width, int height, byte bin, BitDepth bitDepth, bool fastReadout)
    {
        StartX = startX;
        StartY = startY;
        Width = width;
        Height = height;
        Bin = bin;
        BitDepth = bitDepth;
        FastReadout = fastReadout;
    }

    public int Width { get; }
    public int Height { get; }

    public int StartX { get; }
    public int StartY { get; }
    public byte Bin { get; }
    public BitDepth BitDepth { get; }
    public bool FastReadout { get; }

    public static void WithStartX(ref ExposureSettings @this, int startX)
        => @this = new ExposureSettings(startX, @this.StartY, @this.Width, @this.Height, @this.Bin, @this.BitDepth, @this.FastReadout);

    public static void WithStartY(ref ExposureSettings @this, int startY)
        => @this = new ExposureSettings(@this.StartX, startY, @this.Width, @this.Height, @this.Bin, @this.BitDepth, @this.FastReadout);

    public static void WithWidth(ref ExposureSettings @this, int width)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, width, @this.Height, @this.Bin, @this.BitDepth, @this.FastReadout);

    public static void WithHeight(ref ExposureSettings @this, int height)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, height, @this.Bin, @this.BitDepth, @this.FastReadout);

    public static void WithBin(ref ExposureSettings @this, byte bin)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, bin, @this.BitDepth, @this.FastReadout);

    public static void WithBitDepth(ref ExposureSettings @this, BitDepth bitDepth)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, @this.Bin, bitDepth, @this.FastReadout);

    public static void WithFastReadout(ref ExposureSettings @this, bool fastReadout)
        => @this = new ExposureSettings(@this.StartX, @this.StartY, @this.Width, @this.Height, @this.Bin, @this.BitDepth, fastReadout);
}
