namespace Astap.Lib.Devices;

public readonly record struct Filter(string Name, int Offset = 0)
{
    public static readonly Filter None = new(nameof(None));
    public static readonly Filter Unknown = new(nameof(Unknown));
}
