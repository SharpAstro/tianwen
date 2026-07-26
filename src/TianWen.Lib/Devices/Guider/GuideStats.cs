/*

MIT License

Copyright (c) 2018 Andy Galasso

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

namespace TianWen.Lib.Devices.Guider;

public class GuideStats
{
    public double TotalRMS { get; internal set; }
    public double RaRMS { get; internal set; }
    public double DecRMS { get; internal set; }
    public double PeakRa { get; internal set; }
    public double PeakDec { get; internal set; }
    public double? LastRaErr { get; internal set; }
    public double? LastDecErr { get; internal set; }

    /// <summary>Last RA correction pulse in milliseconds (positive = West, negative = East). Null if no correction.</summary>
    public double? LastRaPulseMs { get; internal set; }

    /// <summary>Last Dec correction pulse in milliseconds (positive = North, negative = South). Null if no correction.</summary>
    public double? LastDecPulseMs { get; internal set; }

    public GuideStats Clone() => (GuideStats)MemberwiseClone();

    /// <summary>
    /// Builds a stats snapshot from the five RMS/peak figures alone. Exists so a consumer OUTSIDE this
    /// assembly can reconstruct stats it received over a wire (<c>RemoteSessionMirror</c> mapping a
    /// node's <c>GuiderStateDto</c>) without opening every setter up: the per-sample fields
    /// (<see cref="LastRaErr"/>, <see cref="LastRaPulseMs"/>, ...) stay null because they are not part
    /// of the transported summary, which is exactly what a local guider reports before its first
    /// correction too.
    /// </summary>
    public static GuideStats FromRms(double totalRms, double raRms, double decRms, double peakRa, double peakDec) =>
        new GuideStats
        {
            TotalRMS = totalRms,
            RaRMS = raRms,
            DecRMS = decRms,
            PeakRa = peakRa,
            PeakDec = peakDec,
        };
}
