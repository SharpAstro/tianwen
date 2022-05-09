using System;

namespace Astap.Lib;

public struct FocusSolution
{
    public FocusSolution(double p, double a, double b, double error, int iterations)
    {
        P = p;
        A = a;
        B = b;
        Error = error;
        Iterations = iterations;
    }

    public double P { get; }

    public double A { get; }

    public double B { get; }

    public double Error { get; }

    public int Iterations { get; }
}

public static class Hyperbola
{
    /// <summary>
    /// calculate metric from position and perfectfocusposition using hyperbola parameters.
    /// Example:
    /// The HFD (half flux diameter) of the imaged star disk as function of the focuser position can be described as hyperbola
    /// a,b are the hyperbola parameters, a is the lowest HFD value at focus position, the asymptote y:= +-x*a/b
    /// A hyperbola is defined as:
    /// x=b*sinh(t)
    /// y=a*cosh(t)
    /// Using the arccosh and arsinh functions it is possible to inverse
    /// above calculations and convert x=>t and t->y or y->t and t->x
    /// </summary>
    public static double CalculateValueAtPosition(double position, double perfectfocusposition, double a, double b)
    {
        var x = perfectfocusposition - position;
        var t = Math.Asinh(x / b); // calculate t-position in hyperbola
        return a * Math.Cosh(t); // convert t-position to y/value
    }

    /// <summary>
    /// Calculates focuser steps to perfect focus from HFD and hyperbola parameters
    /// The HFD (half flux diameter) of the imaged star disk as function of the focuser position can be described as hyperbola
    /// a,b are the hyperbola parameters, a is the lowest HFD value at focus position, the asymptote y =  +-x*a/b
    /// A hyperbola is defined as:
    /// x=b*sinh(t)
    /// y=a*cosh(t)
    /// Using the arccosh and arsinh functions it is possible to inverse
    /// above calculations and convert x=>t and t->y or y->t and t->x
    ///
    /// Note using the HFD there are two solutions, either left or right side of the hyperbola
    /// </summary>
    /// <param name="sample"></param>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static double StepsToFocus(double sample, double a, double b)
    {
        var x = sample / a;
        if (x < 1)
        {
            x = 1;/* prevent run time errors */
        }

        var t = Math.Acosh(x);   /* calculate t-position in hyperbola */
        return b * Math.Sinh(t); /* convert t-position to steps to focus */
    }

    /// <summary>
    /// calculates total averaged error between measured V-curve and hyperbola
    /// </summary>
    public static double MeanErrorHyperbola(double[,] data, double perfectfocusposition, double a, double b)
    {
        var total_error = 0.0;
        var n = data.GetLength(0);

        for (var i = 0; i < n; i++)
        {
            var simulation = CalculateValueAtPosition(data[i, 0], perfectfocusposition, a, b);

            // smart error calculation which limits error for outliers
            var error = simulation - data[i, 1]; // error in simulation
            if (error < 0)
            {
                total_error -= error / data[i, 1]; // if data[i,1] is large positive outlier then limit error to data[i,2]/data[i,2]=1 maximum
            }
            else
            {
                total_error += error / simulation; // {if data[i,1] is large negative outlier then limit error to simulation/simulation=1 maximum}
            }
        }

        return total_error / n; // scale to average error per point
    }

    /// <summary>
    /// The input data array should contain several focuser positions with there corresponding metric sample (star disk size, FWHM, ...).
    /// The routine will try to find the best hyperbola curve fit. The focuser position p at the hyperbola minimum is the expected best focuser position
    /// </summary>
    public static FocusSolution FindBestHyperbolaFit(
        double[,] data,
        double threshold = 1E-5,
        int max_iterations = 30
    )
    {
        var old_error = 0.0;
        var lowest_error = 1E99;
        var highest_value = 0.0;
        var lowest_value = 1E99;
        var highest_position = double.NaN;
        var lowest_position = double.NaN;
        var n = data.GetLength(0);

        for (var i = 0; i < n; i++)
        {
            if (data[i, 1] > highest_value)
            {
                highest_value = data[i, 1];
                highest_position = data[i, 0];
            }
            if ((data[i, 1] < lowest_value) && (data[i, 1] > 0.1) /* avoid zero's */)
            {
                lowest_value = data[i, 1];
                lowest_position = data[i, 0];
            }
        }

        if (highest_position < lowest_position)
        {
            highest_position = (lowest_position - highest_position) + lowest_position; // go up always
        }

        // get good starting values for a, b and p
        var a = lowest_value; // a is near the actual value
                              // Alternative hyperbola formula: sqr(y)/sqr(a)-sqr(x)/sqr(b)=1 ==>  sqr(b)=sqr(x)*sqr(a)/(sqr(y)-sqr(a)
        var b = Math.Sqrt(Math.Pow(highest_position - lowest_position, 2) * Math.Pow(a, 2) / (Math.Pow(highest_value, 2) - Math.Pow(a, 2)));
        var p = lowest_position;

        // set starting test range
        var a_range = a;
        var b_range = b;
        var p_range = (highest_position - lowest_position); // large steps since slope could contain some error
        var iteration_cycles = 0;

        do
        {
            var p0 = p;
            var b0 = b;
            var a0 = a;

            a_range *= 0.5; // reduce scan range by 50%
            b_range *= 0.5;
            p_range *= 0.5;

            var p1 = p0 - p_range; // start value
            while (p1 <= p0 + p_range)
            {
                var a1 = a0 - a_range; // start value
                while (a1 <= a0 + a_range)
                {
                    var b1 = b0 - b_range; // start value
                    while (b1 <= b0 + b_range)
                    {
                        var error1 = MeanErrorHyperbola(data, p1, a1, b1); // calculate the curve fit error with these values.
                        if (error1 < lowest_error) // better position found
                        {
                            old_error = lowest_error;
                            lowest_error = error1;
                            a = a1; // best value up to now
                            b = b1;
                            p = p1;
                        }

                        b1 += b_range * 0.1; // do 20 steps within range, many steps guarantees convergence
                    }

                    a1 += a_range * 0.1; // do 20 steps within range
                }

                p1 += p_range * 0.1; // do 20 steps within range
            }

            iteration_cycles++;
        } while (
            (old_error - lowest_error >= threshold)   // lowest error almost reached. Error is expressed in relative error per point
            && (lowest_error > threshold)            // perfect result
            && (iteration_cycles < max_iterations)   // most likely convergence problem
        );
        return new FocusSolution(p, a, b, lowest_error, iteration_cycles);
    }
}
