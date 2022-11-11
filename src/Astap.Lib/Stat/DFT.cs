using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Astap.Lib.Stat;

/**
 * Released under the MIT License
 *
 * DFT Core Functions Copyright (c) 2016 Steven C. Hageman
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to
 * deal in the Software without restriction, including without limitation the
 * rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
 * sell copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */

/// <summary>
/// DFT Base Class - Performs a complex DFT w/Optimizations for .NET >= 4.
/// </summary>
public class DFT
{
    private readonly double _dftScale;     // DFT ONLY Scale Factor
    private readonly uint _lengthTotal;    // mN + mZp
    private readonly uint _lengthHalf;     // (mN + mZp) / 2

    private readonly double[,]? _cosTerm;     // Caching of multiplication terms to save time
    private readonly double[,]? _sinTerm;     // on smaller DFT's
    private readonly bool _outOfMemory;       // True = Caching ran out of memory.


    /// <summary>
    /// Read only Boolean property. True meas the currently defined DFT is using cached memory to speed up calculations.
    /// </summary>
    public bool IsUsingCached => !_outOfMemory;

    /// <summary>
    /// Initializes the DFT.
    /// </summary>
    /// <param name="inputDataLength"></param>
    /// <param name="zeroPaddingLength"></param>
    /// <param name="forceNoCache">True will force the DFT to not use pre-calculated caching.</param>
    public DFT(uint inputDataLength, uint zeroPaddingLength = 0, bool forceNoCache = false)
    {
        // Save the sizes for later
        _lengthTotal = inputDataLength + zeroPaddingLength;
        _lengthHalf = _lengthTotal / 2 + 1;

        // Set the overall scale factor for all the terms
        _dftScale = Math.Sqrt(2) / (inputDataLength + zeroPaddingLength);               // Natural DFT Scale Factor                                           // Window Scale Factor
        _dftScale *= (inputDataLength + zeroPaddingLength) / (double)inputDataLength;   // Account For Zero Padding                           // Zero Padding Scale Factor

        if (forceNoCache)
        {
            // If optional No Cache - just flag that the cache failed
            // then the routines will use the brute force DFT methods.
            _outOfMemory = true;
        }
        else
        {
            // Try to make pre-calculated sin/cos arrays. If not enough memory, then
            // use a brute force DFT.
            // Note: pre-calculation speeds the DFT up by about 5X (on a core i7)
            _outOfMemory = false;
            try
            {
                _cosTerm = new double[_lengthTotal, _lengthTotal];
                _sinTerm = new double[_lengthTotal, _lengthTotal];

                double scaleFactor = 2.0 * Math.PI / _lengthTotal;

                for (int j = 0; j < _lengthHalf; j++)
                {
                    double a = j * scaleFactor;
                    for (int k = 0; k < _lengthTotal; k++)
                    {
                        _cosTerm[j, k] = Math.Cos(a * k) * _dftScale;
                        _sinTerm[j, k] = Math.Sin(a * k) * _dftScale;
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // Could not allocate enough room for the cache terms
                // So, will use brute force DFT
                _outOfMemory = true;
            }
        }
    }


    /// <summary>
    /// Execute the DFT.
    /// </summary>
    /// <param name="timeSeries"></param>
    /// <returns>Complex[] FFT Result</returns>
    public Complex[] Execute(double[] timeSeries)
    {
        Debug.Assert(timeSeries.Length <= _lengthTotal, "The input timeSeries length was greater than the total number of points that was initialized. DFT.Exectue()");

        // Account for zero padding in size of DFT input array
        double[] totalInputData = new double[_lengthTotal];
        Array.Copy(timeSeries, totalInputData, timeSeries.Length);

        return _outOfMemory ? Dft(totalInputData) : DftCached(totalInputData);
    }


    /// <summary>
    /// A brute force DFT - Uses Task / Parallel pattern
    /// </summary>
    /// <param name="timeSeries"></param>
    /// <returns>Complex[] result</returns>
    private Complex[] Dft(double[] timeSeries)
    {
        uint n = _lengthTotal;
        uint m = _lengthHalf;
        double[] re = new double[m];
        double[] im = new double[m];
        Complex[] result = new Complex[m];
        double sf = 2.0 * Math.PI / n;

        Parallel.For(0, m, (j) =>
        //for (UInt32 j = 0; j < m; j++)
        {
            double a = j * sf;
            for (uint k = 0; k < n; k++)
            {
                re[j] += timeSeries[k] * Math.Cos(a * k) * _dftScale;
                im[j] -= timeSeries[k] * Math.Sin(a * k) * _dftScale;
            }

            result[j] = new Complex(re[j], im[j]);
        });

        // DC and Fs/2 Points are scaled differently, since they have only a real part
        result[0] = new Complex(result[0].Real / Math.Sqrt(2), 0.0);
        result[_lengthHalf - 1] = new Complex(result[_lengthHalf - 1].Real / Math.Sqrt(2), 0.0);

        return result;
    }

    /// <summary>
    /// DFT with Pre-calculated Sin/Cos arrays + Task / Parallel pattern.
    /// DFT can only be so big before the computer runs out of memory and has to use
    /// the brute force DFT.
    /// </summary>
    /// <param name="timeSeries"></param>
    /// <returns>Complex[] result</returns>
    private Complex[] DftCached(double[] timeSeries)
    {
        if (_sinTerm is null || _cosTerm is null)
        {
            throw new InvalidOperationException("Cache has not been initialised!");
        }

        uint n = _lengthTotal;
        uint m = _lengthHalf;
        double[] re = new double[m];
        double[] im = new double[m];
        Complex[] result = new Complex[m];

        Parallel.For(0, m, (j) =>
        //for (UInt32 j = 0; j < m; j++)
        {
            for (uint k = 0; k < n; k++)
            {
                re[j] += timeSeries[k] * _cosTerm[j, k];
                im[j] -= timeSeries[k] * _sinTerm[j, k];
            }
            result[j] = new Complex(re[j], im[j]);
        });

        // DC and Fs/2 Points are scaled differently, since they have only a real part
        result[0] = new Complex(result[0].Real / Math.Sqrt(2), 0.0);
        result[_lengthHalf - 1] = new Complex(result[_lengthHalf - 1].Real / Math.Sqrt(2), 0.0);

        return result;
    }

    /// <summary>
    /// Return the Frequency Array for the currently defined DFT.
    /// Takes into account the total number of points and zero padding points that were defined.
    /// </summary>
    /// <param name="samplingFrequencyHz"></param>
    /// <returns></returns>
    public double[] FrequencySpan(double samplingFrequencyHz)
    {
        uint points = _lengthHalf;
        double[] result = new double[points];
        double stopValue = samplingFrequencyHz / 2.0;
        double increment = stopValue / (points - 1.0);

        for (uint i = 0; i < points; i++)
            result[i] += increment * i;

        return result;
    }

}
