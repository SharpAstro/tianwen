using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// The BLIND solve of last resort: nova.astrometry.net's web API, with the user's own API key. A frame no local solver can
/// solve, because its stars are too few or too faint for the shipped catalogues or the pointing is too far from where the
/// mount says (a planetary camera at 1780 mm: 8 or 9 stars, the brightest G 11 to 12, 8.7 degrees from the mount's position),
/// is solved there from no hint at all, scale included, in seconds. Tried after every local solver
/// (<see cref="Priority"/>), and offered only once the user has stored a key (<see cref="CredentialKey"/>): it sends the frame
/// to a service, and nobody's frame leaves the machine unasked.
/// </summary>
/// <remarks>
/// <para><b>What is sent is the frame binned by the MEDIAN of each block</b> (<see cref="MedianBin"/>), never the frame: a
/// hot pixel at full scale still lifts its block's MEAN by thousands of counts, which reads as a star, while a block's median
/// drops an isolated pixel and keeps a star that spans several (about 10 px across at 1780 mm on 2 um pixels). The bin keeps
/// the longer side near 1000 px, which is also what nova's own detection works at, and a small upload. Uploaded not public.</para>
/// <para><b>Its answer is read as a FITS header</b> (the job's <c>wcs_file</c>) through <see cref="WCS.FromHeader"/>, the one
/// reader of a header's reference pixel, and carried back to the full frame by <see cref="UnbinFrom"/>. The job is asked
/// for a linear fit (no SIP): over a planetary camera's quarter degree a distortion term is noise.</para>
/// </remarks>
/// <param name="credentials">Where the key is kept; null in a host with no credential store, where the solver is never offered.</param>
internal sealed class NovaAstrometryPlateSolver(ICredentialStore? credentials, ITimeProvider timeProvider, ILogger<NovaAstrometryPlateSolver> logger)
    : IPlateSolver
{
    /// <inheritdoc cref="BlindSolveService.CredentialKey"/>
    public static string CredentialKey => BlindSolveService.CredentialKey;

    private static readonly Uri Api = new Uri("https://nova.astrometry.net/api/");
    private static readonly Uri Site = new Uri("https://nova.astrometry.net/");

    /// <summary>The longest side the uploaded frame is binned down to, at most.</summary>
    internal const int UploadLongSide = 1024;

    /// <summary>How long a submission may take to become a solved or failed job.</summary>
    internal static readonly TimeSpan SolveTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly SocketsHttpHandler SharedHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    public string Name => "nova.astrometry.net (blind, online)";

    /// <summary>Last of every solver: it needs the internet and sends the frame away.</summary>
    public float Priority => 0.05f;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(credentials?.Get(CredentialKey) is { Length: > 0 });

    public async Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (!Image.TryReadFitsFile(fitsFile, out var image))
        {
            throw new PlateSolverException($"{fitsFile} holds no image to solve");
        }
        return await SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
    }

    public async Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (credentials?.Get(CredentialKey) is not { Length: > 0 } key)
        {
            throw new PlateSolverException("No nova.astrometry.net API key is stored");
        }

        var started = timeProvider.GetTimestamp();
        var bin = BinFactorFor(image.Width, image.Height);
        var upload = UploadBytes(image, bin);
        using var http = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TianWen (https://github.com/SharpAstro/tianwen)");

        var session = await LoginAsync(http, key, cancellationToken);
        var settings = UploadSettings(session, imageDim, bin, searchOrigin, searchRadius);
        var subId = await UploadAsync(http, settings, upload, cancellationToken);
        logger.LogInformation("nova.astrometry.net: submission {SubId}, {Width}x{Height} binned {Bin} from {FullWidth}x{FullHeight}{Hint}",
            subId, image.Width / bin, image.Height / bin, bin, image.Width, image.Height,
            searchOrigin is { } origin ? $", near {origin.CenterRA:F3}h {origin.CenterDec:F2} deg" : ", blind");

        var deadline = timeProvider.GetUtcNow() + SolveTimeout;
        long? jobId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetUtcNow() > deadline)
            {
                throw new PlateSolverException($"nova.astrometry.net did not finish submission {subId} within {SolveTimeout.TotalMinutes:F0} minutes");
            }
            jobId ??= await FirstJobAsync(http, subId, cancellationToken);
            if (jobId is { } job && await JobStatusAsync(http, job, cancellationToken) is var status and ("success" or "failure"))
            {
                if (status == "failure")
                {
                    logger.LogInformation("nova.astrometry.net: job {Job} found no solution", job);
                    return new PlateSolveResult(null, timeProvider.GetElapsedTime(started));
                }
                var binned = await WcsAsync(http, job, cancellationToken);
                var solution = UnbinFrom(binned, bin);
                logger.LogInformation("nova.astrometry.net: job {Job} solved {RA:F4}h {Dec:F3} deg at {Scale:F4}\"/px", job, solution.CenterRA,
                    solution.CenterDec, solution.PixelScaleArcsec);
                return new PlateSolveResult(solution, timeProvider.GetElapsedTime(started));
            }
            await timeProvider.SleepAsync(PollInterval, cancellationToken);
        }
    }

    /// <summary>The bin that keeps the frame's longer side at most <see cref="UploadLongSide"/> pixels.</summary>
    internal static int BinFactorFor(int width, int height) => Math.Max(1, (Math.Max(width, height) + UploadLongSide - 1) / UploadLongSide);

    /// <summary>
    /// The frame binned <paramref name="bin"/> by the MEDIAN of each block (of the channels' mean, for a colour frame): a hot pixel
    /// is one value of the block's <c>bin * bin</c> and is dropped, where a block's mean would carry it as a star.
    /// </summary>
    internal static float[,] MedianBin(Image image, int bin)
    {
        var (channels, width, height) = image.Shape;
        var (w, h) = (width / bin, height / bin);
        var result = new float[h, w];
        var block = ArrayPool<float>.Shared.Rent(bin * bin);
        try
        {
            for (var by = 0; by < h; by++)
            {
                for (var bx = 0; bx < w; bx++)
                {
                    var n = 0;
                    for (var y = by * bin; y < (by + 1) * bin; y++)
                    {
                        for (var x = bx * bin; x < (bx + 1) * bin; x++)
                        {
                            var sum = 0f;
                            for (var c = 0; c < channels; c++)
                            {
                                sum += image[c, y, x];
                            }
                            block[n++] = sum / channels;
                        }
                    }
                    var values = block.AsSpan(0, n);
                    values.Sort();
                    result[by, bx] = (n & 1) == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(block);
        }
        return result;
    }

    /// <summary>
    /// A solution of the frame binned <paramref name="bin"/>, on the full frame: binned pixel <c>i</c> (0-based, centroid frame)
    /// covers full pixels <c>i * bin</c> to <c>i * bin + bin - 1</c>, so its centre is <c>i * bin + (bin - 1) / 2</c>, and a
    /// degree a pixel is <paramref name="bin"/> times smaller. The SIP terms, which a linear job does not carry, are dropped.
    /// </summary>
    internal static WCS UnbinFrom(WCS binned, int bin) => binned with
    {
        CRPix1 = binned.CRPix1 * bin + (bin - 1) / 2.0,
        CRPix2 = binned.CRPix2 * bin + (bin - 1) / 2.0,
        CD1_1 = binned.CD1_1 / bin,
        CD1_2 = binned.CD1_2 / bin,
        CD2_1 = binned.CD2_1 / bin,
        CD2_2 = binned.CD2_2 / bin,
        SipA = null,
        SipB = null,
        SipAP = null,
        SipBP = null,
        SipOrder = 0,
    };

    // The binned frame as a FITS file, written by the one FITS writer: one 32-bit float plane, the frame's own metadata.
    private static byte[] UploadBytes(Image image, int bin)
    {
        var plane = MedianBin(image, bin);
        var (min, max) = (float.MaxValue, float.MinValue);
        foreach (var value in plane)
        {
            (min, max) = (Math.Min(min, value), Math.Max(max, value));
        }
        var binned = new Image([plane], BitDepth.Float32, max, min, 0f, image.ImageMeta);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
        try
        {
            binned.WriteToFitsFile(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The upload's settings: not public, a linear fit, the scale bounds from the frame's nominal scale where it has one (half to
    // twice it on the binned frame, since a nominal focal length can be wrong by a third: 2400 mm written, 1780 mm measured),
    // and the position only where the caller gives one with a radius.
    private static string UploadSettings(string session, ImageDim? imageDim, int bin, WCS? searchOrigin, double? searchRadius)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("session", session);
            json.WriteString("publicly_visible", "n");
            json.WriteString("allow_modifications", "n");
            json.WriteString("allow_commercial_use", "n");
            json.WriteNumber("tweak_order", 1);
            json.WriteBoolean("crpix_center", true);
            if (imageDim is { PixelScale: > 0 and var scale } && double.IsFinite(scale))
            {
                json.WriteString("scale_units", "arcsecperpix");
                json.WriteString("scale_type", "ul");
                json.WriteNumber("scale_lower", scale * bin * 0.5);
                json.WriteNumber("scale_upper", scale * bin * 2.0);
            }
            if (searchOrigin is { } origin && searchRadius is > 0 and var radius && double.IsFinite(origin.CenterRA) && double.IsFinite(origin.CenterDec))
            {
                json.WriteNumber("center_ra", origin.CenterRA * 15.0);
                json.WriteNumber("center_dec", origin.CenterDec);
                json.WriteNumber("radius", radius);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> LoginAsync(HttpClient http, string key, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("apikey", key);
            json.WriteEndObject();
        }
        using var content = new FormUrlEncodedContent([new("request-json", Encoding.UTF8.GetString(buffer.ToArray()))]);
        using var answer = await PostAsync(http, new Uri(Api, "login"), content, cancellationToken);
        if (Status(answer) != "success" || !answer.RootElement.TryGetProperty("session", out var session) || session.GetString() is not { Length: > 0 } id)
        {
            // The answer's own words, never the key.
            throw new PlateSolverException($"nova.astrometry.net refused the API key: {ErrorMessage(answer)}");
        }
        return id;
    }

    private static async Task<long> UploadAsync(HttpClient http, string settings, byte[] fits, CancellationToken cancellationToken)
    {
        // The multipart disposes its parts too; disposing them here as well is harmless and states who owns them.
        using var json = new StringContent(settings, Encoding.UTF8, "text/plain");
        using var file = new ByteArrayContent(fits);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var content = new MultipartFormDataContent();
        content.Add(json, "request-json");
        content.Add(file, "file", "frame.fits");
        using var answer = await PostAsync(http, new Uri(Api, "upload"), content, cancellationToken);
        if (Status(answer) != "success" || !answer.RootElement.TryGetProperty("subid", out var subId))
        {
            throw new PlateSolverException($"nova.astrometry.net refused the upload: {ErrorMessage(answer)}");
        }
        return subId.GetInt64();
    }

    // The submission's first job, once nova has made one; null before.
    private static async Task<long?> FirstJobAsync(HttpClient http, long subId, CancellationToken cancellationToken)
    {
        using var answer = await GetJsonAsync(http, new Uri(Api, $"submissions/{subId}"), cancellationToken);
        if (answer.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                if (job.ValueKind == JsonValueKind.Number)
                {
                    return job.GetInt64();
                }
            }
        }
        return null;
    }

    private static async Task<string?> JobStatusAsync(HttpClient http, long job, CancellationToken cancellationToken)
    {
        using var answer = await GetJsonAsync(http, new Uri(Api, $"jobs/{job}"), cancellationToken);
        return Status(answer);
    }

    // The job's solution, a .wcs file (a FITS header with no data), read as every external solver's .wcs is.
    private static async Task<WCS> WcsAsync(HttpClient http, long job, CancellationToken cancellationToken)
    {
        var bytes = await http.GetByteArrayAsync(new Uri(Site, $"wcs_file/{job}"), cancellationToken);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".wcs");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            using var reader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 2880 * 4);
            using var fits = new Fits(reader);
            return WCS.FromFits(fits) ?? throw new PlateSolverException($"nova.astrometry.net's solution for job {job} holds no WCS");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, Uri uri, HttpContent content, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(uri, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static string? Status(JsonDocument answer)
        => answer.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null;

    private static string ErrorMessage(JsonDocument answer)
        => answer.RootElement.TryGetProperty("errormessage", out var message) && message.GetString() is { Length: > 0 } words
            ? words
            : Status(answer) ?? "no answer";
}
