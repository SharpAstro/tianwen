using System;
using System.Numerics;
using System.Diagnostics;

namespace TianWen.Lib.Stat;

/**
 * Released under the MIT License
 *
 * Core FFT class based on,
 *      Fast C# FFT - Copyright (c) 2010 Gerald T. Beauregard
 *
 * Changes to: Interface, scaling, zero padding, return values.
 * Change to .NET Complex output types and integrated with my DSP Library.
 * Note: Complex Number Type requires .NET >= 4.0
 *
 * These changes as noted above Copyright (c) 2016 Steven C. Hageman
 *
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
/// FFT Base Class - Performs an in-place complex FFT.
/// </summary>
public partial class FFT
{
    private readonly double _fftScale = 1.0;
    private readonly uint _logN = 0;       // log2 of FFT size
    private readonly uint _n = 0;          // Time series length
    private readonly uint _lengthTotal;    // mN + mZp
    private readonly uint _lengthHalf;     // (mN + mZp) / 2
    private readonly FFTElement[] _xs;     // Vector of linked list elements

    /// <summary>
    /// Initialize the FFT.
    /// </summary>
    /// <param name="inputDataLength"></param>
    /// <param name="zeroPaddingLength"></param>
    public FFT(uint inputDataLength, uint zeroPaddingLength = 0)
    {
        _n = inputDataLength;

        // Find the power of two for the total FFT size up to 2^32
        bool foundIt = false;
        for (_logN = 1; _logN <= 32; _logN++)
        {
            double n = Math.Pow(2.0, _logN);
            if (inputDataLength + zeroPaddingLength == n)
            {
                foundIt = true;
                break;
            }
        }

        if (foundIt == false)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroPaddingLength), "inputDataLength + zeroPaddingLength was not an even power of 2! FFT cannot continue.");
        }

        // Set global parameters.
        _lengthTotal = inputDataLength + zeroPaddingLength;
        _lengthHalf = _lengthTotal / 2 + 1;

        // Set the overall scale factor for all the terms
        _fftScale = Math.Sqrt(2) / _lengthTotal;                // Natural FFT Scale Factor                                           // Window Scale Factor
        _fftScale *= _lengthTotal / (double)inputDataLength;    // Zero Padding Scale Factor

        // Allocate elements for linked list of complex numbers.
        _xs = new FFTElement[_lengthTotal];
        for (uint k = 0; k < _lengthTotal; k++)
        {
            _xs[k] = new FFTElement();
        }

        // Set up "next" pointers.
        for (uint k = 0; k < _lengthTotal - 1; k++)
        {
            _xs[k].Next = _xs[k + 1];
        }

        // Specify target for bit reversal re-ordering.
        for (uint k = 0; k < _lengthTotal; k++)
        {
            _xs[k].RevTgt = BitReverse(k, _logN);
        }
    }


    /// <summary>
    /// Executes a FFT of the input time series.
    /// </summary>
    /// <param name="timeSeries"></param>
    /// <returns>Complex[] Spectrum</returns>
    public Complex[] Execute(double[] timeSeries)
    {
        uint numFlies = _lengthTotal >> 1;  // Number of butterflies per sub-FFT
        uint span = _lengthTotal >> 1;      // Width of the butterfly
        uint spacing = _lengthTotal;        // Distance between start of sub-FFTs
        uint wIndexStep = 1;          // Increment for twiddle table index

        Debug.Assert(timeSeries.Length <= _lengthTotal, "The input timeSeries length was greater than the total number of points that was initialized. FFT.Exectue()");

        // Copy data into linked complex number objects
        FFTElement? x = _xs[0];
        uint k = 0;
        for (uint i = 0; i < _n; i++)
        {
            if (x is null)
            {
                break;
            }

            x.Re = timeSeries[k];
            x.Im = 0.0;
            x = x.Next;
            k++;
        }

        // If zero padded, clean the 2nd half of the linked list from previous results
        if (_n != _lengthTotal)
        {
            for (uint i = _n; i < _lengthTotal; i++)
            {
                if (x is null)
                {
                    break;
                }
                x.Re = 0.0;
                x.Im = 0.0;
                x = x.Next;
            }
        }

        // For each stage of the FFT
        for (uint stage = 0; stage < _logN; stage++)
        {
            // Compute a multiplier factor for the "twiddle factors".
            // The twiddle factors are complex unit vectors spaced at
            // regular angular intervals. The angle by which the twiddle
            // factor advances depends on the FFT stage. In many FFT
            // implementations the twiddle factors are cached, but because
            // array lookup is relatively slow in C#, it's just
            // as fast to compute them on the fly.
            double wAngleInc = wIndexStep * -2.0 * Math.PI / _lengthTotal;
            double wMulRe = Math.Cos(wAngleInc);
            double wMulIm = Math.Sin(wAngleInc);

            for (uint start = 0; start < _lengthTotal; start += spacing)
            {
                FFTElement? xTop = _xs[start];
                FFTElement? xBot = _xs[start + span];

                double wRe = 1.0;
                double wIm = 0.0;

                // For each butterfly in this stage
                for (uint flyCount = 0; flyCount < numFlies; ++flyCount)
                {
                    if (xTop is null || xBot is null) break;
                    // Get the top & bottom values
                    double xTopRe = xTop.Re;
                    double xTopIm = xTop.Im;
                    double xBotRe = xBot.Re;
                    double xBotIm = xBot.Im;

                    // Top branch of butterfly has addition
                    xTop.Re = xTopRe + xBotRe;
                    xTop.Im = xTopIm + xBotIm;

                    // Bottom branch of butterfly has subtraction,
                    // followed by multiplication by twiddle factor
                    xBotRe = xTopRe - xBotRe;
                    xBotIm = xTopIm - xBotIm;
                    xBot.Re = xBotRe * wRe - xBotIm * wIm;
                    xBot.Im = xBotRe * wIm + xBotIm * wRe;

                    // Advance butterfly to next top & bottom positions
                    xTop = xTop.Next;
                    xBot = xBot.Next;

                    // Update the twiddle factor, via complex multiply
                    // by unit vector with the appropriate angle
                    // (wRe + j wIm) = (wRe + j wIm) x (wMulRe + j wMulIm)
                    double tRe = wRe;
                    wRe = wRe * wMulRe - wIm * wMulIm;
                    wIm = tRe * wMulIm + wIm * wMulRe;
                }
            }

            numFlies >>= 1;   // Divide by 2 by right shift
            span >>= 1;
            spacing >>= 1;
            wIndexStep <<= 1;     // Multiply by 2 by left shift
        }

        // The algorithm leaves the result in a scrambled order.
        // Unscramble while copying values from the complex
        // linked list elements to a complex output vector & properly apply scale factors.

        x = _xs[0];
        Complex[] unswizzle = new Complex[_lengthTotal];
        while (x != null)
        {
            uint target = x.RevTgt;
            unswizzle[target] = new Complex(x.Re * _fftScale, x.Im * _fftScale);
            x = x.Next;
        }

        // Return 1/2 the FFT result from DC to Fs/2 (The real part of the spectrum)
        //UInt32 halfLength = ((mN + mZp) / 2) + 1;
        Complex[] result = new Complex[_lengthHalf];
        Array.Copy(unswizzle, result, _lengthHalf);

        // DC and Fs/2 Points are scaled differently, since they have only a real part
        result[0] = new Complex(result[0].Real / Math.Sqrt(2), 0.0);
        result[_lengthHalf - 1] = new Complex(result[_lengthHalf - 1].Real / Math.Sqrt(2), 0.0);

        return result;
    }


    /**
     * Do bit reversal of specified number of places of an int
     * For example, 1101 bit-reversed is 1011
     *
     * @param   x       Number to be bit-reverse.
     * @param   numBits Number of bits in the number.
     */
    private static uint BitReverse(uint x, uint numBits)
    {
        uint y = 0;
        for (uint i = 0; i < numBits; i++)
        {
            y <<= 1;
            y |= x & 0x0001;
            x >>= 1;
        }
        return y;
    }


    /// <summary>
    /// Return the Frequency Array for the currently defined FFT.
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

        for (int i = 0; i < points; i++)
            result[i] += increment * i;

        return result;
    }
}