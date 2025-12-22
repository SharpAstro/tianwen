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

using System;

namespace TianWen.Lib.Devices.Guider;

record Accum()
{
    double SumOfSquaredDifference;

    public double Mean { get; private set; }
    public uint Count { get; private set; }
    public double Peak { get; private set; }
    public double? Last { get; private set; }

    public void Reset()
    {
        Count = 0;
        Mean = SumOfSquaredDifference = Peak = 0;
        Last = null;
    }

    public void Add(double x)
    {
        Last = x;
        var absX = Math.Abs(x);
        if (absX > Peak)
        {
            Peak = absX;
        }
        
        ++Count;
        var diff = x - Mean;
        Mean += diff / Count;
        SumOfSquaredDifference += (x - Mean) * diff;
    }

    public double Stdev => Count >= 1 ? Math.Sqrt(SumOfSquaredDifference / Count) : 0.0;

}