namespace Astap.Lib.Sequencing;

public enum SetpointTempKind : byte
{
    Normal,
    CCD,
    Ambient
}

public record SetpointTemp(sbyte TempC, SetpointTempKind Kind);
