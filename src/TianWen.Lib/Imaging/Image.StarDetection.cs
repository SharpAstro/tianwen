using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    const int BoxRadius = 14;
    const float HfdFactor = 1.5f;
    const int MaxScaledRadius = (int)(HfdFactor * BoxRadius) + 1;
    static readonly ImmutableArray<BitMatrix> StarMasks;

    static Image()
    {
        var starMasksBuilder = ImmutableArray.CreateBuilder<BitMatrix>(MaxScaledRadius);
        for (var radius = 1; radius < MaxScaledRadius; radius++)
        {
            MakeStarMask(radius, out var mask);
            starMasksBuilder.Add(mask);
        }

        StarMasks = starMasksBuilder.ToImmutable();
    }

    static void MakeStarMask(int radius, out BitMatrix starMask)
    {
        var diameter = radius << 1;
        var radius_squared = radius * radius;
        starMask = new BitMatrix(diameter + 1, diameter + 1);

        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius_squared)
                {
                    int pixelX = radius + x;
                    int pixelY = radius + y;
                    if (pixelX >= 0 && pixelX <= diameter && pixelY >= 0 && pixelY <= diameter)
                    {
                        starMask[pixelY, pixelX] = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Find background, noise level, number of stars and their HFD, FWHM, SNR, flux and centroid.
    /// </summary>
    /// <param name="channel">Channel</param>
    /// <param name="snrMin">S/N ratio threshold for star detection</param>
    /// <param name="maxStars"></param>
    /// <param name="maxRetries"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public virtual async Task<StarList> FindStarsAsync(int channel, float snrMin = 20f, int maxStars = 500, int maxRetries = 2, CancellationToken cancellationToken = default)
    {
        const int ChunkSize = 2 * MaxScaledRadius;
        const float HalfChunkSizeInv = 1.0f / 2.0f * ChunkSize;
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }
        if (imageMeta.SensorType is SensorType.RGGB && ChannelCount is 1)
        {
            // debayer to mono
            var monoImage = await DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken);
            return await monoImage.FindStarsAsync(channel, snrMin, maxStars, maxRetries, cancellationToken);
        }

        var (background, star_level, noise_level, hist_threshold) = Background(channel);

        var detection_level = MathF.Max(3.5f * noise_level, star_level); /* level above background. Start with a high value */
        var retries = maxRetries;

        if (background >= hist_threshold || background <= 0)  /* abnormal file */
        {
            return new StarList([]);
        }

        var starList = new ConcurrentBag<ImagedStar>();
        var img_star_area = new BitMatrix(height, width);

        // we use interleaved processing of rows (so that we do not have to lock to protect the bitmatrix
        var halfChunkCount = (int)Math.Ceiling(height * HalfChunkSizeInv);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4, CancellationToken = cancellationToken };

        do
        {
            for (var i = 0; i <= 1; i++)
            {
                await Parallel.ForAsync(0, halfChunkCount, parallelOptions, async (halfChunk, cancellationToken) =>
                {
                    await Task.Run(() =>
                    {
                        var chunk = 2 * halfChunk + i;
                        var chunkEnd = Math.Min(height, (chunk + 1) * ChunkSize);
                        for (var fitsY = chunk * ChunkSize; fitsY < chunkEnd; fitsY++)
                        {
                            for (var fitsX = 0; fitsX < width; fitsX++)
                            {
                                // new star. For analyse used sigma is 5, so not too low.
                                var value = data[channel, fitsY, fitsX];
                                if (float.IsNaN(value))
                                {
                                    img_star_area[fitsY, fitsX] = true; /* ignore NaN values */
                                }
                                else if (value - background > detection_level
                                    && !img_star_area[fitsY, fitsX]
                                    && AnalyseStar(channel, fitsX, fitsY, BoxRadius, out var star)
                                    && star.HFD is > 0.8f and <= BoxRadius * 2 /* at least 2 pixels in size */
                                    && star.SNR >= snrMin
                                )
                                {
                                    starList.Add(star);
                                    var scaledHfd = HfdFactor * star.HFD;
                                    var r = (int)MathF.Round(scaledHfd); /* radius for marking star area, factor 1.5 is chosen emperiacally. */
                                    var xc_offset = (int)MathF.Round(star.XCentroid - scaledHfd); /* star center as integer */
                                    var yc_offset = (int)MathF.Round(star.YCentroid - scaledHfd);

                                    var mask = StarMasks[Math.Max(r - 1, 0)];

                                    img_star_area.SetRegionClipped(yc_offset, xc_offset, mask);
                                }
                            }
                        }
                    }, cancellationToken);
                });
            }

            /* In principle not required. Try again with lower detection level */
            if (detection_level <= 7 * noise_level)
            {
                retries = -1; /* stop */
            }
            else
            {
                retries--;
                detection_level = MathF.Max(6.999f * noise_level, MathF.Min(30 * noise_level, detection_level * 6.999f / 30)); /* very high -> 30 -> 7 -> stop.  Or  60 -> 14 -> 7.0. Or for very short exposures 3.5 -> stop */
            }
        } while (starList.Count < maxStars && retries > 0);/* reduce detection level till enough stars are found. Note that faint stars have less positional accuracy */

        return new StarList(starList);
    }

    /// <summary>
    /// calculate star HFD and FWHM, SNR, xc and yc are center of gravity.All x, y coordinates in array[0..] positions
    /// </summary>
    /// <param name="x1">x</param>
    /// <param name="y1">y</param>
    /// <param name="boxRadius">box radius</param>
    /// <returns>true if a star was detected</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool AnalyseStar(int channel, int x1, int y1, int boxRadius, out ImagedStar star)
    {
        const int maxAnnulusBg = 328; // depends on boxSize <= 50
        Debug.Assert(boxRadius <= 50, nameof(boxRadius) + " should be <= 50 to prevent runtime errors");

        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }

        var r1_square = boxRadius * boxRadius; /*square radius*/
        var r2 = boxRadius + 1; /*annulus width plus 1*/
        var r2_square = r2 * r2;

        var valMax = 0.0f;
        float sumVal;
        float bg;
        float sd_bg;

        float xc = float.NaN, yc = float.NaN;
        int r_aperture = -1;

        if (x1 - r2 <= 0 || x1 + r2 >= width - 1 || y1 - r2 <= 0 || y1 + r2 >= height - 1)
        {
            star = default;
            return false;
        }

        Span<float> backgroundScratch = stackalloc float[maxAnnulusBg];
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
                        var value = data[channel, y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            backgroundScratch[backgroundIndex++] = value;
                        }
                    }
                }
            }

            var background = backgroundScratch[..backgroundIndex];
            bg = Median(background);

            float minNonZeroBgValue = 0;
            /* fill background with offsets */
            for (var i = 0; i < background.Length; i++)
            {
                var bg_i = background[i];
                // assumes that median sorts ascending
                if (minNonZeroBgValue == 0)
                {
                    minNonZeroBgValue = bg_i;
                }
                background[i] = MathF.Abs(bg_i - bg);
            }

            //median absolute deviation (MAD)
            var mad_bg = Median(background);
            sd_bg = mad_bg * MAD_TO_SD;

            // add some value for images with zero noise background.
            // This will prevent that background is seen as a star. E.g. some jpg processed by nova.astrometry.net
            if (sd_bg == 0)
            {
                sd_bg = BlackLevel > 0 ? BlackLevel : minNonZeroBgValue;
            }

            // reduce square annulus radius until it is symmetric to remove stars
            bool boxed;
            do
            {
                // Get center of gravity whithin star detection box and count signal pixels, repeat reduce annulus radius till symmetry to remove stars
                sumVal = 0.0f;
                var sumValX = 0.0f;
                var sumValY = 0.0f;
                var signal_counter = 0;

                for (var i = -boxRadius; i <= boxRadius; i++)
                {
                    for (var j = -boxRadius; j <= boxRadius; j++)
                    {
                        var value = data[channel, y1 + i, x1 + j];
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0f * sd_bg)
                            {
                                sumVal += bg_sub_value;
                                sumValX += bg_sub_value * j;
                                sumValY += bg_sub_value * i;
                                signal_counter++; /* how many pixels are illuminated */
                            }
                        }
                    }
                }

                if (sumVal <= 12 * sd_bg)
                {
                    star = default; /*no star found, too noisy */
                    return false;
                }

                var xg = sumValX / sumVal;
                var yg = sumValY / sumVal;

                xc = x1 + xg;
                yc = y1 + yg;
                /* center of gravity found */

                if (xc - boxRadius < 0 || xc + boxRadius > width - 1 || yc - boxRadius < 0 || yc + boxRadius > height - 1)
                {
                    star = default; /* prevent runtime errors near sides of images */
                    return false;
                }

                var rs2_1 = boxRadius + boxRadius + 1;
                boxed = signal_counter >= 2.0f / 9 * (rs2_1 * rs2_1);/*are inside the box 2 of the 9 of the pixels illuminated? Works in general better for solving then ovality measurement as used in the past*/

                if (!boxed)
                {
                    if (boxRadius > 4)
                    {
                        boxRadius -= 2;
                    }
                    else
                    {
                        boxRadius--; /*try a smaller window to exclude nearby stars*/
                    }
                }

                /* check on hot pixels */
                if (signal_counter <= 1)
                {
                    star = default; /*one hot pixel*/
                    return false;
                }
            } while (!boxed && boxRadius > 1); /*loop and reduce aperture radius until star is boxed*/

            boxRadius += 2; /* add some space */

            // Build signal histogram from center of gravity
            Span<int> distance_histogram = stackalloc int[boxRadius + 1]; // this has a fixed upper bound

            for (var i = -boxRadius; i <= boxRadius; i++)
            {
                for (var j = -boxRadius; j <= boxRadius; j++)
                {
                    var distance = (int)MathF.Round(MathF.Sqrt(i * i + j * j)); /* distance from gravity center */
                    if (distance <= boxRadius) /* build histogram for circle with radius boxRadius */
                    {
                        var value = SubpixelValue(channel, xc + i, yc + j);
                        if (!float.IsNaN(value))
                        {
                            var bg_sub_value = value - bg;
                            if (bg_sub_value > 3.0 * sd_bg) /* 3 * sd should be signal */
                            {
                                distance_histogram[distance]++; /* build distance histogram up to circle with diameter rs */

                                if (bg_sub_value > valMax)
                                {
                                    valMax = bg_sub_value; /* record the peak value of the star */
                                }
                            }
                        }
                    }
                }
            }

            var distance_top_value = 0;
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
            } while (r_aperture < boxRadius && (!histStart || distance_histogram[r_aperture] > 0.1f * distance_top_value));

            if (r_aperture >= boxRadius)
            {
                star = default; /* star is equal or larger then box, abort */
                return false;
            }

            if (r_aperture > 2)
            {
                /* if more than 35% surface is illuminated */
                var r_aperture2_2 = 2 * r_aperture - 2;
                if (illuminated_pixels < 0.35f * (r_aperture2_2 * r_aperture2_2))
                {
                    star = default; /* not a star disk but stars, abort */
                    return false;
                }
            }
        }
        catch
        {
            star = default;
            return false;
        }

        // Get HFD
        var pixel_counter = 0;
        sumVal = 0.0f; // reset
        var sumValR = 0.0f;

        // Get HFD using the aproximation routine assuming that HFD line divides the star in equal portions of gravity:
        for (var i = -r_aperture; i <= r_aperture; i++) /*Make steps of one pixel*/
        {
            for (var j = -r_aperture; j <= r_aperture; j++)
            {
                var val = SubpixelValue(channel, xc + i, yc + j) - bg; /* the calculated center of gravity is a floating point position and can be anywhere, so calculate pixel values on sub-pixel level */
                var r = MathF.Sqrt(i * i + j * j); /* distance from star gravity center */
                sumVal += val;/* sumVal will be star total star flux*/
                sumValR += val * r; /* method Kazuhisa Miyashita, see notes of HFD calculation method, note calculate HFD over square area. Works more accurate then for round area */
                if (val >= valMax * 0.5)
                {
                    // How many pixels are above half maximum
                    pixel_counter++;
                }
            }
        }

        var flux = MathF.Max(sumVal, 0.00001f); /* prevent dividing by zero or negative values */
        var hfd = MathF.Max(0.7f, 2 * sumValR / flux);
        var star_fwhm = 2 * MathF.Sqrt(pixel_counter / MathF.PI);/*calculate from surface (by counting pixels above half max) the diameter equals FWHM */
        var snr = flux / MathF.Sqrt(flux + r_aperture * r_aperture * MathF.PI * sd_bg * sd_bg);

        star = new ImagedStar(hfd, star_fwhm, snr, flux, xc, yc);
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
}
