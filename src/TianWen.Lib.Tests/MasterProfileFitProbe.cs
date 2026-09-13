using System;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The store's own master statistic applied to any master on disk: per channel, the stars the
/// dataset measure detects (<see cref="Image.FindStarsAsync"/> at snr 5 over up to 3000 stars) and
/// <see cref="PsfProfileFit.Measure"/> over them, exactly the calls behind <c>SessionPsf.MasterProfiles</c>
/// in <c>psf-sessions.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>Exists so a comparison between integration paths (task 33: staged against drizzle at three
/// pixfracs on one night) reads both sides in the number the store already speaks, rather than in a
/// probe's crop statistic that is 1.35 to 1.5 times the profile fit on the same stars (E2.10's lesson
/// on which width is which). Put the store's own retained master in the list and its line must
/// reproduce the store's row (the Orion 2025-10-15 master reads 2.514 px on green): that is the
/// probe's self-check, not an assertion, because the store's numbers are what this reproduces.</para>
/// <para>Opt-in through <c>TIANWEN_E33_MASTERS</c>, a semicolon-separated list of FITS paths.</para>
/// </remarks>
public sealed class MasterProfileFitProbe(ITestOutputHelper output)
{
    private const string MastersVar = "TIANWEN_E33_MASTERS";

    [Fact]
    public async Task ReportEachMastersProfileFitPerChannel()
    {
        var list = Environment.GetEnvironmentVariable(MastersVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(list), $"{MastersVar} not set");
        var ct = TestContext.Current.CancellationToken;

        output.WriteLine($"{"master",-72} ch  stars   fwhm px  beta   moffat-rms  gauss-rms  stacked");
        foreach (var path in list!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(path))
            {
                output.WriteLine($"{path}: MISSING");
                continue;
            }
            if (!Image.TryReadFitsFile(path, out var master))
            {
                output.WriteLine($"{path}: unreadable");
                continue;
            }
            var (channels, width, height) = master.Shape;
            var name = Path.GetFileName(Path.GetDirectoryName(path) ?? "") + "/" + Path.GetFileName(path);
            output.WriteLine($"{name} {width}x{height} x{channels}");
            for (var c = 0; c < channels; c++)
            {
                var stars = await master.FindStarsAsync(channel: c, snrMin: 5f, maxStars: 3000, cancellationToken: ct);
                var fit = PsfProfileFit.Measure(master, c, stars);
                output.WriteLine(fit is { } f
                    ? $"{name,-72} {c}  {stars.Count,5}   {f.Fwhm,7:F3}  {f.MoffatBeta,5:F2}   {f.MoffatLogRms,10:F3}  {f.GaussianLogRms,9:F3}  {f.StarsStacked,7}"
                    : $"{name,-72} {c}  {stars.Count,5}   not measurable");
            }
        }
    }
}
