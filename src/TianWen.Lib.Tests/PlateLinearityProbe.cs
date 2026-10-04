using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: which starless plates the Stars mode refuses as "not linear". The exporter puts a plate on its MASTER's unit
/// scale and asks the NAFNet pre-stretch's auto-detect (the shifted median of channel 0 over covered pixels under
/// <see cref="AiNafnetInputs.StretchAutoDetectMedianThreshold"/>); this asks the same of every plate in the store, beside the
/// master's own answer on its own scale, and writes one row per session. Leo Triplet (its sky about a third of full scale) is
/// the one known refusal.
/// <para>Set <c>TIANWEN_SATEDGE_BAKE</c>, <c>TIANWEN_SATEDGE_PLATES</c> and <c>TIANWEN_SATEDGE_OUT</c> (a CSV path).</para>
/// </summary>
[Collection("Imaging")]
public sealed class PlateLinearityProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task AskEveryPlateTheExportersLinearityQuestion()
    {
        var bake = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_BAKE");
        var platesRoot = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_PLATES");
        var outPath = Environment.GetEnvironmentVariable("TIANWEN_SATEDGE_OUT");
        Assert.SkipWhen(bake is null || platesRoot is null || outPath is null, "TIANWEN_SATEDGE_* not set");
        var ct = TestContext.Current.CancellationToken;
        var psfStore = await DatasetPsfStore.ReadAsync(Path.Combine(bake, "stats", DatasetPsfStore.FileName), null, ct);
        var context = new DatasetDegradationExporter.StarsContext(Path.Combine(platesRoot, "plates"), psfStore);
        var threshold = AiNafnetInputs.StretchAutoDetectMedianThreshold;

        var csv = new StringBuilder("session,channels,plate_min,plate_shifted_median,plate_refused,master_shifted_median,master_refused\n");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int asked = 0, refused = 0;
        foreach (var sessionId in psfStore.Keys.Order(StringComparer.Ordinal))
        {
            if (!context.HasPlate(sessionId) || !File.Exists(RetainedMasterStore.PathFor(bake, sessionId)) || !seen.Add(context.PlatePath(sessionId)))
            {
                continue;
            }
            if (!RetainedMasterStore.TryRead(bake, sessionId, out var master) || !Image.TryReadFitsFile(context.PlatePath(sessionId), out var plate))
            {
                output.WriteLine($"{sessionId}: unreadable");
                continue;
            }
            var absent = plate.AbsentPixels();
            var divisor = DatasetTileExporter.UnitDivisor(master);
            var unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
            var unitMaster = DatasetTileExporter.ToUnitRange(master, divisor);
            var (plateMin, plateMedian) = unitPlate.MinAndShiftedMedian(0, absent);
            var (_, masterMedian) = unitMaster.MinAndShiftedMedian(0, master.AbsentPixels());
            var plateRefused = !(plateMedian < threshold);
            asked++;
            refused += plateRefused ? 1 : 0;
            csv.Append(CultureInfo.InvariantCulture,
                $"\"{sessionId}\",{master.ChannelCount},{plateMin:G5},{plateMedian:G5},{(plateRefused ? 1 : 0)},{masterMedian:G5},{(masterMedian < threshold ? 0 : 1)}\n");
            if (plateRefused)
            {
                output.WriteLine($"REFUSED {sessionId}: shifted median {plateMedian:F3} (min {plateMin:F3}), master's own {masterMedian:F3}");
            }
            if (!ReferenceEquals(unitPlate, plate))
            {
                unitPlate.Release();
            }
            if (!ReferenceEquals(unitMaster, master))
            {
                unitMaster.Release();
            }
            plate.Release();
            master.Release();
        }
        await File.WriteAllTextAsync(outPath, csv.ToString(), ct);
        output.WriteLine($"{refused} of {asked} plates refused (threshold {threshold})");
    }
}
