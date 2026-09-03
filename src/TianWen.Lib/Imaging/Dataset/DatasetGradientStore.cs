using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// Durable per-master store for the gradient report's INPUTS (<see cref="JsonLinesFile"/>), one line
    /// per master as its measurement completes, keyed by master file name with the last record winning.
    /// The same shape as <see cref="DatasetPsfStore"/> and for the same reason: a killed or resumed run
    /// keeps every master it finished, the report re-renders from the store rather than from the run
    /// that happened to write it, and nothing is ever erased.
    /// </summary>
    public static class DatasetGradientStore
    {
        /// <summary>Store file name, written beside the rendered report under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string FileName = DatasetGradientReport.StoreFileName;

        /// <summary>Reads the store into a master-keyed map, last record per key winning; a missing file yields an empty map.</summary>
        public static Task<Dictionary<string, DatasetGradientReport.MasterGradient>> ReadAsync(
            string path, ILogger? logger = null, CancellationToken cancellationToken = default) =>
            JsonLinesFile.ReadLastPerKeyAsync(
                path, DatasetGradientJsonContext.Default.MasterGradient, static r => r.Master,
                "gradient store", logger, cancellationToken);

        /// <summary>Appends one master's record: one line, one write, after the measurement is complete.</summary>
        public static Task AppendAsync(string path, DatasetGradientReport.MasterGradient record, CancellationToken cancellationToken = default) =>
            JsonLinesFile.AppendRecordAsync(path, record, DatasetGradientJsonContext.Default.MasterGradient, cancellationToken);
    }

    // NaN is a legitimate value here (an unsolved frame has no direction on the sky, a master with no
    // site has no altitude), so the named literals are allowed rather than failing the write.
    [JsonSerializable(typeof(DatasetGradientReport.MasterGradient))]
    [JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    internal partial class DatasetGradientJsonContext : JsonSerializerContext;
}
