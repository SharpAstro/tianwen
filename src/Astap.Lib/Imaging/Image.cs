using nom.tam.fits;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using static Astap.Lib.StatisticsHelper;

namespace Astap.Lib.Imaging;

public record class Image(double[,] Data, int BitDepth, int Width, int Height)
{
    public static bool TryReadFitsFile(string filePath, [NotNullWhen(true)] out Image? image)
    {
        using var bufferedReader = new nom.tam.util.BufferedFile(filePath, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
        return TryReadFitsFile(new Fits(bufferedReader), out image);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image)
    {
        var hdu = fitsFile.ReadHDU();
        if (hdu?.Axes?.Length == 2 && hdu.Data is ImageData imageData)
        {
            var height = hdu.Axes[0];
            var width = hdu.Axes[1];
            var bitDepth = hdu.BitPix;
            var bzero = hdu.BZero;
            var bscale = hdu.BScale;
            var dataArray = imageData.DataArray;
            if (dataArray is object[] heightArray)
            {
                var imgArray = new double[width, height];
                for (int h = 0; h < height; h++)
                {
                    if (heightArray[h] is short[] widthArray)
                    {
                        for (int w = 0; w < width; w++)
                        {
                            imgArray[w, h] = bscale * widthArray[w] + bzero;
                        }
                    }
                }
                image = new Image(imgArray, bitDepth, width, height);
                return true;
            }
        }

        image = default;
        return false;
    }

    /// <summary>
    /// Find background, noise level, number of stars and their HFD, FWHM, SNR, flux and centroid.
    /// </summary>
    /// <param name="snr_min">S/N ratio threshold for star detection</param>
    /// <param name="max_stars"></param>
    /// <param name="max_retries"></param>
    /// <returns></returns>
    public IReadOnlyList<ImagedStar> FindStars(double snr_min = 20, int max_stars = 500, int max_retries = 2)
    {
        var (background, star_level, noise_level) = Background();

        var detection_level = Math.Max(3.5 * noise_level, star_level); /* level above background. Start with a high value */
        var retries = max_retries;

        if (background is >= 60000 or <= 0)  /* abnormal file */
        {
            return Array.Empty<ImagedStar>();
        }

        var starList = new List<ImagedStar>(max_stars / 2);
        var img_sa = new BitMatrix(Width, Height);

        do
        {
            if (retries < max_retries)
            {
                // clear from last iteration to avoid spurious data
                starList.Clear();
                img_sa.Clear();
            }

            for (var fitsY = 0; fitsY < Height; fitsY++)
            {
                for (var fitsX = 0; fitsX < Width; fitsX++)
                {
                    if (!img_sa[fitsX, fitsY]/* star free area */ && Data[fitsX, fitsY] - background > detection_level)  /* new star. For analyse used sigma is 5, so not too low. */
                    {
                        if (AnalyseStar(fitsX, fitsY, 14/* box size */, out var star) && star.HFD <= 30 && star.SNR > snr_min && star.HFD > 0.8 /* two pixels minimum */ )
                        {
                            starList.Add(star);

                            var diam = (int)Math.Round(3.0 * star.HFD); /* for marking star area. Emperical a value between 2.5*hfd and 3.5*hfd gives same performance. Note in practise a star PSF has larger wings  predicted by a Gaussian function */
                            var sqr_diam = (int)Math.Pow(diam, 2);
                            var xci = (int)Math.Round(star.XCentroid);/* star center as integer */
                            var yci = (int)Math.Round(star.YCentroid);

                            for (var n = -diam; n <= +diam; n++)  /* mark the whole circular star area width diameter "diam" as occupied to prevent uble detections */
                            {
                                for (var m = -diam; m <= +diam; m++)
                                {
                                    var j = n + yci;
                                    var i = m + xci;
                                    if (j >= 0 && i >= 0 && j < Height && i < Width && m * m + n * n <= sqr_diam)
                                    {
                                        img_sa[i, j] = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            /* In principle not required. Try again with lower detection level */
            if (detection_level <= 7 * noise_level)
            {
                retries = -1; /* stop */
            }
            else
            {
                retries--;
                detection_level = Math.Max(6.999 * noise_level, Math.Min(30 * noise_level, detection_level * 6.999 / 30)); /* very high -> 30 -> 7 -> stop.  Or  60 -> 14 -> 7.0. Or for very short exposures 3.5 -> stop */
            }
        } while (starList.Count < max_stars && retries > 0);/* reduce detection level till enough stars are found. Note that faint stars have less positional accuracy */

        return starList;
    }

    const int MAX_COL = 65000;

    public ImageHistogram Histogram()
    {
        var hist_total = 0;
        var total_value = 0;
        var count = 1; // prevent divide by zero

        var offsetW = (int)(Width * 0.042); // if Libraw is used, ignored unused sensor areas up to 4.2 %
        var offsetH = (int)(Height * 0.015); // if Libraw is used, ignored unused sensor areas up to 1.5 %

        var histogram = new int[MAX_COL];

        for (var i = 0 + offsetH; i <= Height - 1 - offsetH; i++)
        {
            for (var j = 0 + offsetW; j <= Width - 1 - offsetW; j++)
            {
                var col = (int)Math.Round(Data[j, i]);

                // ignore black overlap areas and bright stars
                if (col >= 1 && col < MAX_COL)
                {
                    histogram[col]++; // calculate histogram
                    hist_total++;
                    total_value += col;
                    count++;
                }
            }
        }

        var hist_mean = (int)Math.Round(1.0 * total_value / count);

        return new ImageHistogram(histogram, hist_mean, hist_total);
    }

    /// <summary>
    /// get background and star level from peek histogram
    /// </summary>
    /// <returns>background and star level</returns>
    public (double background, double starLevel, double noise_level) Background()
    {
        // get histogram of img_loaded and his_total
        var histogram = Histogram();
        var background = Data[0, 0]; // define something for images containing 0 or 65535 only

        // find peak in histogram which should be the average background}=
        var pixels = 0;
        var max_range = histogram.Mean;
        int i;
        // mean value from histogram
        for (i = 1; i <= max_range; i++)
        {
            // find peak, ignore value 0 from oversize
            if (histogram.Histogram[i] > pixels) // find colour peak
            {
                pixels = histogram.Histogram[i];
                background = i;
            }
        }

        // check alternative mean value
        if (histogram.Mean > 1.5 * background) // 1.5 * most common
        {
            background = histogram.Mean; // strange peak at low value, ignore histogram and use mean
        }

        // find star level and background noise level
        // calculate star level
        if (BitDepth == 8 || BitDepth == 24)
        {
            max_range = 255;
        }
        else
        {
            max_range = MAX_COL; // histogram runs from 65000
        }
        i = max_range;

        double starLevel = 0.0;
        int above = 0;

        while (starLevel == 0 && i > background + 1)
        {
            // find star level 0.003 of values
            i--;
            above += histogram.Histogram[i];
            if (above > 0.001 * histogram.Total)
            {
                starLevel = i;
            }
        }

        if (starLevel <= background)
        {
            starLevel = background + 1; // no or very few stars
        }
        else
        {
            // star level above background. Important subtract 1 for saturated images. Otherwise no stars are detected
            starLevel = starLevel - background - 1;
        }

        // calculate noise level
        var stepSize = (int)Math.Round(Height / 71.0); // get about 71x71 = 5000 samples.So use only a fraction of the pixels

        // prevent problems with even raw OSC images
        if (stepSize % 2 == 0)
        {
            stepSize++;
        }

        var sd = 99999.0;
        double sd_old;
        var iterations = 0;

        // repeat until sd is stable or 7 iterations
        do
        {
            var fitsX = 15;
            var counter = 1; // never divide by zero

            sd_old = sd;
            while (fitsX <= Width - 1 - 15)
            {
                var fitsY = 15;
                while (fitsY <= Height - 1 - 15)
                {
                    var value = Data[fitsX, fitsY];
                    // not an outlier, noise should be symmetrical so should be less then twice background
                    if (value < background * 2 && value != 0)
                    {
                        // ignore outliers after first run
                        if (iterations == 0 || Math.Abs(value - background) <= 3 * sd_old)
                        {
                            sd += Math.Pow(value - background, 2);
                            // keep record of number of pixels processed
                            counter++;
                        }
                    }
                    fitsY += stepSize; // skip pixels for speed
                }
                fitsX += stepSize; // skip pixels for speed
            }
            sd = Math.Sqrt(sd / counter); // standard deviation
            iterations++;
        } while (sd_old - sd >= 0.05 * sd && iterations < 7); // repeat until sd is stable or 7 iterations

        return (background, starLevel, Math.Round(sd));
    }

    /// <summary>
    /// calculate star HFD and FWHM, SNR, xc and yc are center of gravity.All x, y coordinates in array[0..] positions
    /// </summary>
    /// <param name="x1">x</param>
    /// <param name="y1">y</param>
    /// <param name="rs">box size</param>
    /// <returns>true if a star was detected</returns>
    public bool AnalyseStar(int x1, int y1, int rs, out ImagedStar star)
    {
        const int maxAnnulusBg = 328; // depends on rs <= 50
        Debug.Assert(rs <= 50, "rs should be <= 50 to prevent runtime errors");

        var r1_square = rs * rs; /*square radius*/
        var r2 = rs + 1; /*annulus width us 1*/
        var r2_square = r2 * r2;

        var valMax = 0.0;
        var hfd = 999.0;
        var star_fwhm = 999.0;
        var snr = 0.0;
        var flux = 0.0;
        double sumVal;
        double bg;
        double sd_bg;

        double xc = double.NaN, yc = double.NaN;
        int r_aperture = -1;

        if (x1 - r2 <= 0 || x1 + r2 >= Width - 1 || y1 - r2 <= 0 || y1 + r2 >= Height - 1)
        {
            star = new(hfd, star_fwhm, snr, flux, xc, yc);
            return false;
        }

        Span<double> backgroundScratch = stackalloc double[maxAnnulusBg];
        int backgroundIndex = 0;

        try
        {
            /*calculate the mean outside the the detection area*/
            for (var i = -r2; i <= r2; i++)
            {
                for (var j = -r2; j <= r2; j++)
                {
                    var distance = i * i + j * j; /*working with sqr(distance) is faster then applying sqrt*/
                    /*annulus, circular area outside rs, typical one pixel wide*/
                    if (distance > r1_square && distance <= r2_square)
                    {
                        backgroundScratch[backgroundIndex++] = Data[x1 + i, y1 + j];
                    }
                }
            }

            var background = backgroundScratch[..backgroundIndex];
            bg = Median(background);

            /* fill background with offsets */
            for (var i = 0; i < background.Length; i++)
            {
                background[i] = Math.Abs(background[i] - bg);
            }

            var mad_bg = Median(background); //median absolute deviation (MAD)
            sd_bg = mad_bg * 1.4826; /* Conversion from mad to sd for a normal distribution. See https://en.wikipedia.org/wiki/Median_absolute_deviation */
            sd_bg = Math.Max(sd_bg, 1); /* add some value for images with zero noise background. This will prevent that background is seen as a star. E.g. some jpg processed by nova.astrometry.net*/

            bool boxed;
            do /* reduce square annulus radius till symmetry to remove stars */
            {
                // Get center of gravity whithin star detection box and count signal pixels, repeat reduce annulus radius till symmetry to remove stars
                sumVal = 0.0;
                var sumValX = 0.0;
                var sumValY = 0.0;
                var signal_counter = 0;

                for (var i = -rs; i <= rs; i++)
                {
                    for (var j = -rs; j <= rs; j++)
                    {
                        var val = Data[x1 + i, y1 + j] - bg;
                        if (val > 3.0 * sd_bg)
                        {
                            sumVal += val;
                            sumValX += val * i;
                            sumValY += val * j;
                            signal_counter++; /* how many pixels are illuminated */
                        }
                    }
                }

                if (sumVal <= 12 * sd_bg)
                {
                    star = new(hfd, star_fwhm, snr, flux, xc, yc); /*no star found, too noisy, return with hfd=999 */
                    return false;
                }

                var xg = sumValX / sumVal;
                var yg = sumValY / sumVal;

                xc = x1 + xg;
                yc = y1 + yg;
                /* center of gravity found */

                if (xc - rs < 0 || xc + rs > Width - 1 || yc - rs < 0 || yc + rs > Height - 1)
                {
                    star = new(hfd, star_fwhm, snr, 0, xc, xc); /* prevent runtime errors near sides of images */
                    return false;
                }

                boxed = signal_counter >= 2.0 / 9 * Math.Pow(rs + rs + 1, 2);/*are inside the box 2 of the 9 of the pixels illuminated? Works in general better for solving then ovality measurement as used in the past*/

                if (!boxed)
                {
                    if (rs > 4)
                    {
                        rs -= 2;
                    }
                    else
                    {
                        rs--; /*try a smaller window to exclude nearby stars*/
                    }
                }

                /* check on hot pixels */
                if (signal_counter <= 1)
                {
                    star = new(hfd, star_fwhm, snr, flux, xc, xc); /*one hot pixel*/
                    return false;
                }
            } while (!boxed && rs > 1); /*loop and reduce aperture radius until star is boxed*/

            rs += 2; /* add some space */

            // Build signal histogram from center of gravity
            Span<int> distance_histogram = stackalloc int[rs + 1]; // this has a fixed upper bound

            for (var i = -rs; i <= rs; i++)
            {
                for (var j = -rs; j <= rs; j++)
                {
                    var distance = (int)Math.Round(Math.Sqrt(i * i + j * j)); /* distance from gravity center */
                    if (distance <= rs) /* build histogram for circel with radius rs */
                    {
                        var val = SubpixelValue(xc + i, yc + j) - bg;
                        if (val > 3.0 * sd_bg) /* 3 * sd should be signal */
                        {
                            distance_histogram[distance]++; /* build distance histogram up to circel with diameter rs */

                            if (val > valMax)
                            {
                                valMax = val; /* record the peak value of the star */
                            }
                        }
                    }
                }
            }

            var distance_top_value = 0.0;
            var histStart = false;
            var illuminated_pixels = 0;
            do
            {
                r_aperture++;
                illuminated_pixels += distance_histogram[r_aperture];
                if (distance_histogram[r_aperture] > 0)
                {
                    histStart = true; /*continue until we found a value>0, center of defocused star image can be black having a central obstruction in the telescope*/
                }

                if (distance_top_value < distance_histogram[r_aperture])
                {
                    distance_top_value = distance_histogram[r_aperture]; /* this should be 2*pi*r_aperture if it is nice defocused star disk */
                }
                /* find a distance where there is no pixel illuminated, so the border of the star image of interest */
            } while (r_aperture < rs && (!histStart || distance_histogram[r_aperture] > 0.1 * distance_top_value));

            if (r_aperture >= rs)
            {
                star = new(hfd, star_fwhm, snr, flux, xc, xc); /* star is equal or larger then box, abort */
                return false;
            }

            if (r_aperture > 2 && illuminated_pixels < 0.35 /*35% surface*/ * Math.Pow(r_aperture + r_aperture - 2, 2))
            {
                star = new(hfd, star_fwhm, snr, flux, xc, xc); /* not a star disk but stars, abort with hfd 999 */
                return false;
            }
        }
        catch (Exception ex) when (Environment.UserInteractive)
        {
            GC.KeepAlive(ex);
            throw;
        }
        catch
        {
            star = new(hfd, star_fwhm, snr, flux, xc, xc);
            return false;
        }

        // Get HFD
        var pixel_counter = 0;
        sumVal = 0.0; // reset
        var sumValR = 0.0;

        // Get HFD using the aproximation routine assuming that HFD line divides the star in equal portions of gravity:
        for (var i = -r_aperture; i <= r_aperture; i++) /*Make steps of one pixel*/
        {
            for (var j = -r_aperture; j <= r_aperture; j++)
            {
                var val = SubpixelValue(xc + i, yc + j) - bg; /* the calculated center of gravity is a floating point position and can be anywhere, so calculate pixel values on sub-pixel level */
                var r = Math.Sqrt(i * i + j * j); /* distance from star gravity center */
                sumVal += val;/* sumVal will be star total star flux*/
                sumValR += val * r; /* method Kazuhisa Miyashita, see notes of HFD calculation method, note calculate HFD over square area. Works more accurate then for round area */
                if (val >= valMax * 0.5)
                {
                    pixel_counter++; /* How many pixels are above half maximum */
                }
            }
        }

        flux = Math.Max(sumVal, 0.00001); /* prevent dividing by zero or negative values */
        hfd = Math.Max(0.7, 2 * sumValR / flux);

        star_fwhm = 2 * Math.Sqrt(pixel_counter / Math.PI);/*calculate from surface (by counting pixels above half max) the diameter equals FWHM */

        snr = flux / Math.Sqrt(flux + Math.Pow(r_aperture, 2) * Math.PI * Math.Pow(sd_bg, 2));

        star = new(hfd, star_fwhm, snr, flux, xc, yc);
        return true;
        /*For both bright stars (shot-noise limited) or skybackground limited situations
        snr := signal/noise
        snr := star_signal/sqrt(total_signal)
        snr := star_signal/sqrt(star_signal + sky_signal)
        equals
        snr:=flux/sqrt(flux + r*r*pi* sd^2).

        r is the diameter used for star flux measurement. Flux is the total star flux detected above 3* sd.

        Assuming unity gain ADU/e-=1
        See https://en.wikipedia.org/wiki/Signal-to-noise_ratio_(imaging)
        https://www1.phys.vt.edu/~jhs/phys3154/snr20040108.pdf
        http://spiff.rit.edu/classes/phys373/lectures/signal/signal_illus.html*/


        /*==========Notes on HFD calculation method=================
          Documented this HFD definition also in https://en.wikipedia.org/wiki/Half_flux_diameter
          References:
          https://astro-limovie.info/occultation_observation/halffluxdiameter/halffluxdiameter_en.html       by Kazuhisa Miyashita. No sub-pixel calculation
          https://www.lost-infinity.com/night-sky-image-processing-part-6-measuring-the-half-flux-diameter-hfd-of-a-star-a-simple-c-implementation/
          http://www.ccdware.com/Files/ITS%20Paper.pdf     See page 10, HFD Measurement Algorithm

          HFD, Half Flux Diameter is defined as: The diameter of circle where total flux value of pixels inside is equal to the outside pixel's.
          HFR, half flux radius:=0.5*HFD
          The pixel_flux:=pixel_value - background.

          The approximation routine assumes that the HFD line divides the star in equal portions of gravity:
              sum(pixel_flux * (distance_from_the_centroid - HFR))=0
          This can be rewritten as
             sum(pixel_flux * distance_from_the_centroid) - sum(pixel_values * (HFR))=0
             or
             HFR:=sum(pixel_flux * distance_from_the_centroid))/sum(pixel_flux)
             HFD:=2*HFR

          This is not an exact method but a very efficient routine. Numerical checking with an a highly oversampled artificial Gaussian shaped star indicates the following:

          Perfect two dimensional Gaussian shape with σ=1:   Numerical HFD=2.3548*σ                     Approximation 2.5066, an offset of +6.4%
          Homogeneous disk of a single value  :              Numerical HFD:=disk_diameter/sqrt(2)       Approximation disk_diameter/1.5, an offset of -6.1%

          The approximate routine is robust and efficient.

          Since the number of pixels illuminated is small and the calculated center of star gravity is not at the center of an pixel, above summation should be calculated on sub-pixel level (as used here)
          or the image should be re-sampled to a higher resolution.

          A sufficient signal to noise is required to have valid HFD value due to background noise.

          Note that for perfect Gaussian shape both the HFD and FWHM are at the same 2.3548 σ.
          */


        /*=============Notes on FWHM:=====================
           1)	Determine the background level by the averaging the boarder pixels.
           2)	Calculate the standard deviation of the background.

               Signal is anything 3 * standard deviation above background

           3)	Determine the maximum signal level of region of interest.
           4)	Count pixels which are equal or above half maximum level.
           5)	Use the pixel count as area and calculate the diameter of that area  as diameter:=2 *sqrt(count/pi).*/
    }

    /// <summary>
    /// calculate image pixel value on subpixel level
    /// </summary>
    /// <param name="x1"></param>
    /// <param name="y1"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public double SubpixelValue(double x1, double y1)
    {
        //  x_trunc, y_trunc: integer;
        //     x_frac,y_frac  : double;

        var x_trunc = (int)Math.Truncate(x1);
        var y_trunc = (int)Math.Truncate(y1);

        if (x_trunc <= 0 || x_trunc >= Width - 2 || y_trunc <= 0 || y_trunc >= Height - 2)
        {
            return 0;
        }

        var x_frac = x1 - x_trunc;
        var y_frac = y1 - y_trunc;
        try
        {
            var result = Data[x_trunc, y_trunc] * (1 - x_frac) * (1 - y_frac); // pixel left top, 1
            result += Data[x_trunc + 1, y_trunc] * x_frac * (1 - y_frac);      // pixel right top, 2
            result += Data[x_trunc, y_trunc + 1] * (1 - x_frac) * y_frac;      // pixel left bottom, 3
            result += Data[x_trunc + 1, y_trunc + 1] * x_frac * y_frac;        // pixel right bottom, 4
            return result;
        }
        catch (Exception ex) when (Environment.UserInteractive)
        {
            GC.KeepAlive(ex);
            throw;
        }
        catch
        {
            return 0;
        }
    }
}