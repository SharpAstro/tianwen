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

class Accum
{
    uint n;
    double a;
    double q;
    double peak;

    public Accum()
    {
        Reset();
    }

    public double? Last { get; private set; }

    public void Reset()
    {
        n = 0;
        a = q = peak = 0;
        Last = null;
    }

    public void Add(double x)
    {
        Last = x;
        double ax = Math.Abs(x);
        if (ax > peak) peak = ax;
        ++n;
        double d = x - a;
        a += d / n;
        q += (x - a) * d;
    }
    public double Mean()
    {
        return a;
    }
    public double Stdev()
    {
        return n >= 1 ? Math.Sqrt(q / n) : 0.0;
    }
    public double Peak()
    {
        return peak;
    }
}