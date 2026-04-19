namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// Guide step DTO matching ninaAPI v2 <c>/v2/api/equipment/guider/graph</c> response shape.
/// Field names match what Touch N Stars expects (PascalCase, NINA-specific naming).
/// </summary>
public sealed class NinaGuideStepDto
{
    public required string Timestamp { get; init; }
    public required double RADistanceRawDisplay { get; init; }
    public required double DECDistanceRawDisplay { get; init; }
    public required double RADuration { get; init; }
    public required double DECDuration { get; init; }
    public required bool Dither { get; init; }
    public required bool Settling { get; init; }
}
