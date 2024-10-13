namespace Astap.Lib.Devices;

public record Filter(string Name, int Offset = 0)
{
    public static readonly Filter None = new(nameof(None));
    public static readonly Filter Unknown = new(nameof(Unknown));
}
