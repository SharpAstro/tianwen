using System;

namespace TianWen.Lib.Imaging;

[Flags]
public enum Bandpass
{
    None = 0,
    Red   = 0b0000_0001,
    Green = 0b0000_0010,
    Blue  = 0b0000_0100,

    OIII  = 0b1000_0000,
    Ha    = 0b0100_0000,
    Hb    = 0b0010_0000,
    SII   = 0b0001_0000,

    Luminance = Red | Green | Blue | OIII | Ha | Hb | SII
}
