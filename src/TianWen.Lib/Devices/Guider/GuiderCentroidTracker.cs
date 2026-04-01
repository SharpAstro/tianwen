using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Multi-star guide star tracker using flux-weighted centroid.
/// On initial acquisition, scans the full frame to find all suitable guide stars
/// above an SNR threshold, selects the best candidates, and tracks them
/// frame-to-frame within ROI windows around each star's last known position.
/// Reports averaged position deltas for guide corrections.
/// </summary>
internal sealed class GuiderCentroidTracker
{
    private const int DefaultSearchRadius = 16;
    private const int MinSearchRadius = 8;
    private const float DefaultMinSNR = 3.0f;
    private const int DefaultMaxStars = 8;
    private const int MinStarSeparation = 32; // pixels — avoid selecting stars too close together

    private readonly List<TrackedStar> _stars = new List<TrackedStar>();
    private bool _acquired;
    private int _searchRadius;
    private int _maxStars;

    /// <summary>
    /// Minimum SNR threshold for guide star selection.
    /// </summary>
    public float MinSNR { get; set; } = DefaultMinSNR;

    /// <summary>
    /// Number of stars currently being tracked.
    /// </summary>
    public int TrackedStarCount => _stars.Count;

    /// <summary>
    /// Whether guide stars have been acquired.
    /// </summary>
    public bool IsAcquired => _acquired;

    /// <summary>
    /// The most recent centroid result from <see cref="ProcessFrame"/>.
    /// </summary>
    public GuiderCentroidResult? LastResult { get; private set; }

    /// <summary>
    /// Maximum number of guide stars to track simultaneously.
    /// </summary>
    public int MaxStars
    {
        get => _maxStars;
        set => _maxStars = Math.Max(1, value);
    }

    /// <summary>
    /// Search radius around each star's last known position (pixels).
    /// This defines the ROI size for per-star tracking.
    /// </summary>
    public int SearchRadius
    {
        get => _searchRadius;
        set => _searchRadius = Math.Max(MinSearchRadius, value);
    }

    /// <summary>
    /// The tracked stars and their current positions (read-only snapshot).
    /// </summary>
    internal IReadOnlyList<TrackedStar> Stars => _stars;

    /// <summary>
    /// Gets the bounding box ROI (region of interest) that encloses all tracked stars,
    /// with padding for the search radius. Returns null if no stars are acquired.
    /// For multi-star guiding, the camera can be configured to read only this sub-frame.
    /// </summary>
    public (int X, int Y, int Width, int Height)? GetBoundingBoxROI(int imageWidth, int imageHeight)
    {
        if (_stars.Count == 0)
        {
            return null;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var star in _stars)
        {
            if (star.LastX < minX) minX = star.LastX;
            if (star.LastY < minY) minY = star.LastY;
            if (star.LastX > maxX) maxX = star.LastX;
            if (star.LastY > maxY) maxY = star.LastY;
        }

        // Add search radius + annulus margin as padding
        var padding = _searchRadius + 4;
        var x = Math.Max(0, (int)(minX - padding));
        var y = Math.Max(0, (int)(minY - padding));
        var x2 = Math.Min(imageWidth, (int)(maxX + padding + 1));
        var y2 = Math.Min(imageHeight, (int)(maxY + padding + 1));

        return (x, y, x2 - x, y2 - y);
    }

    public GuiderCentroidTracker(int searchRadius = DefaultSearchRadius, int maxStars = DefaultMaxStars)
    {
        _searchRadius = Math.Max(MinSearchRadius, searchRadius);
        _maxStars = Math.Max(1, maxStars);
    }

    /// <summary>
    /// Process a guide camera frame. On the first call, scans the full frame
    /// to find and select guide stars. On subsequent calls, tracks each star
    /// within its ROI and returns the averaged delta.
    /// </summary>
    /// <param name="frame">Guide camera image data [height, width].</param>
    /// <returns>The averaged position delta from lock positions, or null if all stars lost.</returns>
    public GuiderCentroidResult? ProcessFrame(float[,] frame)
    {
        var height = frame.GetLength(0);
        var width = frame.GetLength(1);

        var result = !_acquired
            ? TryAcquire(frame, width, height)
            : TryTrackAll(frame, width, height);

        if (result is { } r)
        {
            // Extract 1D intensity profiles through the primary star center
            var cx = (int)Math.Round(r.X);
            var cy = (int)Math.Round(r.Y);
            var (h, v) = ExtractStarProfile(frame, width, height, cx, cy, _searchRadius);
            var enriched = r with { HProfile = h, VProfile = v };
            LastResult = enriched;
            return enriched;
        }

        return null;
    }

    /// <summary>
    /// Resets the tracker, requiring a new acquisition on the next frame.
    /// </summary>
    public void Reset()
    {
        _acquired = false;
        _stars.Clear();
    }

    /// <summary>
    /// Sets new lock positions at each star's current position.
    /// Useful after calibration to establish the guide reference.
    /// </summary>
    public void SetLockPosition()
    {
        if (!_acquired)
        {
            return;
        }

        for (var i = 0; i < _stars.Count; i++)
        {
            var star = _stars[i];
            _stars[i] = star with { LockX = star.LastX, LockY = star.LastY };
        }
    }

    /// <summary>
    /// Offsets the lock position by the given pixel amounts.
    /// Used for dithering: the guide loop then naturally corrects the star
    /// back to the new lock position, creating the dither offset.
    /// </summary>
    /// <param name="dx">X offset in pixels.</param>
    /// <param name="dy">Y offset in pixels.</param>
    public void OffsetLockPosition(double dx, double dy)
    {
        if (!_acquired)
        {
            return;
        }

        for (var i = 0; i < _stars.Count; i++)
        {
            var star = _stars[i];
            _stars[i] = star with { LockX = star.LockX + dx, LockY = star.LockY + dy };
        }
    }

    private GuiderCentroidResult? TryAcquire(float[,] frame, int width, int height)
    {
        // Scan full frame for candidate stars
        var candidates = FindCandidateStars(frame, width, height);
        if (candidates.Count == 0)
        {
            return null;
        }

        // Sort by flux descending (brightest first)
        candidates.Sort((a, b) => b.Flux.CompareTo(a.Flux));

        // Select well-separated stars up to MaxStars
        _stars.Clear();
        foreach (var candidate in candidates)
        {
            if (_stars.Count >= _maxStars)
            {
                break;
            }

            // Check minimum separation from already selected stars
            var tooClose = false;
            foreach (var selected in _stars)
            {
                var dx = candidate.X - selected.LastX;
                var dy = candidate.Y - selected.LastY;
                if (dx * dx + dy * dy < MinStarSeparation * MinStarSeparation)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                _stars.Add(new TrackedStar(candidate.X, candidate.Y, candidate.X, candidate.Y, candidate.Flux, candidate.SNR));
            }
        }

        if (_stars.Count == 0)
        {
            return null;
        }

        _acquired = true;

        // Return result for the primary (brightest) star with zero delta
        var primary = _stars[0];
        return new GuiderCentroidResult(primary.LastX, primary.LastY, 0, 0, primary.Flux, primary.SNR, _stars.Count);
    }

    private List<CandidateStar> FindCandidateStars(float[,] frame, int width, int height)
    {
        var candidates = new List<CandidateStar>();
        var margin = _searchRadius + 4; // annulus margin

        // Build a sorted list of pixel peaks, then refine each with centroid.
        // First pass: collect all local maxima above background.
        var peaks = new List<(int X, int Y, float Val)>();

        for (var y = margin; y < height - margin; y++)
        {
            for (var x = margin; x < width - margin; x++)
            {
                var val = frame[y, x];
                // Simple local max check: brighter than all 4-connected neighbours
                if (val > frame[y - 1, x] && val > frame[y + 1, x] &&
                    val > frame[y, x - 1] && val > frame[y, x + 1])
                {
                    peaks.Add((x, y, val));
                }
            }
        }

        // Sort by brightness descending
        peaks.Sort((a, b) => b.Val.CompareTo(a.Val));

        // Refine each peak with centroid, skip if too close to an existing candidate
        foreach (var (px, py, _) in peaks)
        {
            var alreadyFound = false;
            foreach (var existing in candidates)
            {
                var ddx = px - existing.X;
                var ddy = py - existing.Y;
                if (ddx * ddx + ddy * ddy < _searchRadius * _searchRadius)
                {
                    alreadyFound = true;
                    break;
                }
            }

            if (alreadyFound)
            {
                continue;
            }

            if (TryCentroid(frame, width, height, px, py, _searchRadius,
                out var cx, out var cy, out var flux, out var snr) && snr >= MinSNR)
            {
                candidates.Add(new CandidateStar(cx, cy, flux, snr));
            }

            // Stop early once we have plenty of candidates
            if (candidates.Count >= _maxStars * 3)
            {
                break;
            }
        }

        return candidates;
    }

    private GuiderCentroidResult? TryTrackAll(float[,] frame, int width, int height)
    {
        var totalDeltaX = 0.0;
        var totalDeltaY = 0.0;
        var totalWeight = 0.0;
        var trackedCount = 0;
        var totalFlux = 0.0;
        var minSNR = double.MaxValue;

        for (var i = _stars.Count - 1; i >= 0; i--)
        {
            var star = _stars[i];
            var centerX = (int)Math.Round(star.LastX);
            var centerY = (int)Math.Round(star.LastY);

            if (!TryCentroid(frame, width, height, centerX, centerY, _searchRadius,
                out var cx, out var cy, out var flux, out var snr) || snr < MinSNR)
            {
                // Star lost — remove from tracking list
                _stars.RemoveAt(i);
                continue;
            }

            _stars[i] = star with { LastX = cx, LastY = cy, Flux = flux, SNR = snr };

            var deltaX = cx - star.LockX;
            var deltaY = cy - star.LockY;

            // Weight by flux (brighter stars get more influence)
            totalDeltaX += deltaX * flux;
            totalDeltaY += deltaY * flux;
            totalWeight += flux;
            totalFlux += flux;
            trackedCount++;

            if (snr < minSNR)
            {
                minSNR = snr;
            }
        }

        if (trackedCount == 0)
        {
            _acquired = false;
            return null;
        }

        // Return flux-weighted average delta
        var avgDeltaX = totalDeltaX / totalWeight;
        var avgDeltaY = totalDeltaY / totalWeight;

        // Report primary star position (first in list, brightest)
        var primary = _stars[0];
        return new GuiderCentroidResult(primary.LastX, primary.LastY, avgDeltaX, avgDeltaY, totalFlux, minSNR, trackedCount);
    }

    /// <summary>
    /// Computes flux-weighted centroid within a circular aperture around (cx, cy).
    /// Background is estimated from the annulus just outside the aperture.
    /// </summary>
    internal static bool TryCentroid(
        float[,] frame, int width, int height,
        int cx, int cy, int radius,
        out double centroidX, out double centroidY,
        out double totalFlux, out double snr)
    {
        centroidX = centroidY = totalFlux = snr = 0;

        // Bounds check
        if (cx - radius - 1 < 0 || cx + radius + 1 >= width ||
            cy - radius - 1 < 0 || cy + radius + 1 >= height)
        {
            return false;
        }

        // Estimate background from annulus (radius+1 to radius+3)
        var bgSum = 0.0;
        var bgSumSq = 0.0;
        var bgCount = 0;
        var innerR2 = radius * radius;
        var outerR = radius + 3;
        var outerR2 = outerR * outerR;

        for (var dy = -outerR; dy <= outerR; dy++)
        {
            for (var dx = -outerR; dx <= outerR; dx++)
            {
                var d2 = dx * dx + dy * dy;
                if (d2 > innerR2 && d2 <= outerR2)
                {
                    var py = cy + dy;
                    var px = cx + dx;
                    if (py >= 0 && py < height && px >= 0 && px < width)
                    {
                        var val = (double)frame[py, px];
                        bgSum += val;
                        bgSumSq += val * val;
                        bgCount++;
                    }
                }
            }
        }

        if (bgCount < 8)
        {
            return false;
        }

        var bg = bgSum / bgCount;
        var bgVariance = bgSumSq / bgCount - bg * bg;
        var bgSigma = Math.Sqrt(Math.Max(0, bgVariance));

        // Flux-weighted centroid within aperture
        var sumFlux = 0.0;
        var sumFluxX = 0.0;
        var sumFluxY = 0.0;
        var peakVal = 0.0;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy <= innerR2)
                {
                    var val = (double)frame[cy + dy, cx + dx] - bg;
                    if (val > 0)
                    {
                        sumFlux += val;
                        sumFluxX += val * dx;
                        sumFluxY += val * dy;
                        if (val > peakVal)
                        {
                            peakVal = val;
                        }
                    }
                }
            }
        }

        if (sumFlux <= 0 || peakVal <= 0)
        {
            return false;
        }

        centroidX = cx + sumFluxX / sumFlux;
        centroidY = cy + sumFluxY / sumFlux;
        totalFlux = sumFlux;
        snr = bgSigma > 0 ? peakVal / bgSigma : peakVal;

        return true;
    }

    /// <summary>
    /// Extracts horizontal and vertical intensity profiles through the star center,
    /// background-subtracted. Profile length = 2 * radius + 1.
    /// </summary>
    internal static (float[] H, float[] V) ExtractStarProfile(
        float[,] frame, int width, int height, int cx, int cy, int radius)
    {
        var size = 2 * radius + 1;
        var h = new float[size];
        var v = new float[size];

        // Estimate background from corners of the extraction box
        var bgSum = 0.0;
        var bgCount = 0;
        var outerR = radius + 3;
        var innerR2 = radius * radius;
        var outerR2 = outerR * outerR;
        for (var dy = -outerR; dy <= outerR; dy++)
        {
            for (var dx = -outerR; dx <= outerR; dx++)
            {
                var d2 = dx * dx + dy * dy;
                if (d2 > innerR2 && d2 <= outerR2)
                {
                    var py = cy + dy;
                    var px = cx + dx;
                    if (py >= 0 && py < height && px >= 0 && px < width)
                    {
                        bgSum += frame[py, px];
                        bgCount++;
                    }
                }
            }
        }
        var bg = bgCount > 0 ? (float)(bgSum / bgCount) : 0f;

        for (var i = 0; i < size; i++)
        {
            var offset = i - radius;
            // Horizontal: row = cy, col = cx + offset
            var hx = cx + offset;
            h[i] = hx >= 0 && hx < width ? Math.Max(0, frame[cy, hx] - bg) : 0;
            // Vertical: row = cy + offset, col = cx
            var vy = cy + offset;
            v[i] = vy >= 0 && vy < height ? Math.Max(0, frame[vy, cx] - bg) : 0;
        }

        return (h, v);
    }

    private readonly record struct CandidateStar(double X, double Y, double Flux, double SNR);
}

/// <summary>
/// State of a single tracked guide star.
/// </summary>
/// <param name="LockX">Lock position X (reference for delta computation).</param>
/// <param name="LockY">Lock position Y.</param>
/// <param name="LastX">Last measured X position (sub-pixel).</param>
/// <param name="LastY">Last measured Y position (sub-pixel).</param>
/// <param name="Flux">Last measured flux.</param>
/// <param name="SNR">Last measured SNR.</param>
internal record struct TrackedStar(
    double LockX, double LockY,
    double LastX, double LastY,
    double Flux, double SNR);

/// <summary>
/// Result of a single guide frame centroid measurement.
/// </summary>
/// <param name="X">Primary star X position (sub-pixel).</param>
/// <param name="Y">Primary star Y position (sub-pixel).</param>
/// <param name="DeltaX">Flux-weighted average offset from lock positions in X (pixels).</param>
/// <param name="DeltaY">Flux-weighted average offset from lock positions in Y (pixels).</param>
/// <param name="Flux">Total integrated flux across all tracked stars.</param>
/// <param name="SNR">Minimum SNR among tracked stars.</param>
/// <param name="HProfile">Horizontal intensity profile through the primary star center (background-subtracted).</param>
/// <param name="VProfile">Vertical intensity profile through the primary star center (background-subtracted).</param>
/// <param name="TrackedStarCount">Number of stars successfully tracked this frame.</param>
internal readonly record struct GuiderCentroidResult(
    double X, double Y,
    double DeltaX, double DeltaY,
    double Flux, double SNR,
    int TrackedStarCount,
    float[]? HProfile = null,
    float[]? VProfile = null);
