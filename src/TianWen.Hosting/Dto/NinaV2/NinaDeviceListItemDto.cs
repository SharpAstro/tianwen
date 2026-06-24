namespace TianWen.Hosting.Dto.NinaV2;

/// <summary>
/// One entry in a ninaAPI v2 <c>list-devices</c> / <c>rescan</c> response. TNS uses this to
/// populate its device-selection dropdown. Concrete (not an anonymous type) so the source-gen
/// JSON context can serialize it under native AOT.
/// </summary>
public sealed class NinaDeviceListItemDto
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }
}
