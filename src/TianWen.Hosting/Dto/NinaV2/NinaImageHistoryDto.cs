using System;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Image history entry matching ninaAPI v2 <c>/v2/api/image-history</c> response shape.
/// </summary>
public sealed class NinaImageHistoryDto
{
    public required int Id { get; init; }
    public required string FileName { get; init; }
    public required string Filter { get; init; }
    public required double ExposureTime { get; init; }
    public required string DateTime { get; init; }
    public required double HFR { get; init; }
    public required int Stars { get; init; }

    public static NinaImageHistoryDto FromEntry(ExposureLogEntry entry, int index)
    {
        return new NinaImageHistoryDto
        {
            Id = index,
            FileName = $"frame_{entry.Timestamp:yyyyMMdd_HHmmss}_{entry.FrameNumber:D4}",
            Filter = entry.FilterName,
            ExposureTime = entry.Exposure.TotalSeconds,
            DateTime = entry.Timestamp.ToString("o"),
            HFR = entry.MedianHfd,
            Stars = entry.StarCount,
        };
    }
}
