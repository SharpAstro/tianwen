using System;
using System.Numerics;

namespace TianWen.Lib.Stat;

public partial class DSP
{
    /*
    * Released under the MIT License
    *
    * DSP Library for C# - Copyright(c) 2016 Steven C. Hageman.
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

    public static class Generate
    {
        /// <summary>
        /// Generate linearly spaced array. Like the Octave function of the same name.
        /// EX: DSP.Generate.LinSpace(1, 10, 10) -> Returns array: 1, 2, 3, 4....10.
        /// </summary>
        /// <param name="startVal">Any value</param>
        /// <param name="stopVal">Any value > startVal</param>
        /// <param name="points">Number of points to generate</param>
        /// <returns>double[] array</returns>
        public static double[] LinSpace(double startVal, double stopVal, uint points)
        {
            double[] result = new double[points];
            double increment = (stopVal - startVal) / (points - 1.0);

            for (uint i = 0; i < points; i++)
                result[i] = startVal + increment * i;

            return result;
        }


        /// <summary>
        /// Generates a Sine Wave Tone using Sampling Terms.
        /// </summary>
        /// <param name="amplitudeVrms"></param>
        /// <param name="frequencyHz"></param>
        /// <param name="samplingFrequencyHz"></param>
        /// <param name="points"></param>
        /// <param name="dcV">[Optional] DC Voltage offset</param>
        /// <param name="phaseDeg">[Optional] Phase of signal in degrees</param>
        /// <returns>double[] array</returns>
        public static double[] ToneSampling(double amplitudeVrms, double frequencyHz, double samplingFrequencyHz, uint points, double dcV = 0.0, double phaseDeg = 0)
        {
            double ph_r = phaseDeg * Math.PI / 180.0;
            double ampPeak = Math.Sqrt(2) * amplitudeVrms;

            double[] rval = new double[points];
            for (uint i = 0; i < points; i++)
            {
                double time = i / samplingFrequencyHz;
                rval[i] = ampPeak * Math.Sin(2.0 * Math.PI * time * frequencyHz + ph_r) + dcV;
            }
            return rval;
        }


        /// <summary>
        /// Generates a Sine Wave Tone using Number of Cycles Terms.
        /// </summary>
        /// <param name="amplitudeVrms"></param>
        /// <param name="cycles"></param>
        /// <param name="points"></param>
        /// <param name="dcV">[Optional] DC Voltage offset</param>
        /// <param name="phaseDeg">[Optional] Phase of signal in degrees</param>
        /// <returns>double[] array</returns>
        public static double[] ToneCycles(double amplitudeVrms, double cycles, uint points, double dcV = 0.0, double phaseDeg = 0)
        {
            double ph_r = phaseDeg * Math.PI / 180.0;
            double ampPeak = Math.Sqrt(2) * amplitudeVrms;

            double[] rval = new double[points];
            for (uint i = 0; i < points; i++)
            {
                rval[i] = ampPeak * Math.Sin(2.0 * Math.PI * i / points * cycles + ph_r) + dcV;
            }
            return rval;
        }


        /// <summary>
        /// Generates a normal distribution noise signal of the specified power spectral density (Vrms / rt-Hz).
        /// </summary>
        /// <param name="amplitudePsd (Vrms / rt-Hz)"></param>
        /// <param name="samplingFrequencyHz"></param>
        /// <param name="points"></param>
        /// <returns>double[] array</returns>
        public static double[] NoisePsd(double amplitudePsd, double samplingFrequencyHz, uint points)
        {
            // Calculate what the noise amplitude needs to be in Vrms/rt_Hz
            double arms = amplitudePsd * Math.Sqrt(samplingFrequencyHz / 2.0);

            // Make an n length noise vector
            return NoiseRms(arms, points);
        }



        /// <summary>
        /// Generates a normal distribution noise signal of the specified Volts RMS.
        /// </summary>
        /// <param name="amplitudeVrms"></param>
        /// <param name="points"></param>
        /// <param name="dcV"></param>
        /// <returns>double[] array</returns>
        public static double[] NoiseRms(double amplitudeVrms, uint points, double dcV = 0.0)
        {
            // Make an n length noise vector
            var noiseVector = Noise(_randomFactory.Value, points, amplitudeVrms);

            return VectorMath.Add(noiseVector, dcV);
        }


        //=====[ Gaussian Noise ]=====

        private static readonly Lazy<Random> _randomFactory = new(() => new Random(), false); // Class level variable

        private static double[] Noise(Random random, uint size, double scaling_vrms)
        {

            // Based on - Polar method (Marsaglia 1962)

            // Scaling used,
            // * For DFT Size => "Math.Sqrt(size)"
            // * The Sqrt(2) is a scaling factor to get the
            // output spectral power to be what the desired "scaling_vrms"
            // was as requested. The scaling will produce a "scaling_vrms"
            // value that is correct for Vrms/Rt(Hz) in the frequency domain
            // of a "1/N" scaled DFT or FFT.
            // Most DFT / FFT's are 1/N scaled - check your documentation to be sure...

            double output_scale = scaling_vrms;

            double[] data = new double[size];
            double sum = 0;

            for (uint n = 0; n < size; n++)
            {
                double s;
                double v1;
                do
                {
                    v1 = 2.0 * random.NextDouble() - 1.0;
                    double v2 = 2.0 * random.NextDouble() - 1.0;

                    s = v1 * v1 + v2 * v2;
                } while (s >= 1.0);

                if (s == 0.0)
                    data[n] = 0.0;
                else
                    data[n] = v1 * Math.Sqrt(-2.0 * Math.Log(s) / s) * output_scale;

                sum += data[n];
            }

            // Remove the average value
            double average = sum / size;
            for (uint n = 0; n < size; n++)
            {
                data[n] -= average;
            }

            // Return the Gaussian noise
            return data;
        }
    }
}