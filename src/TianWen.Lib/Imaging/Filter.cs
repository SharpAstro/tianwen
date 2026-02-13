namespace TianWen.Lib.Imaging;

public readonly record struct Filter(string Name)
{
    public static readonly Filter None = new(nameof(None));
    public static readonly Filter Unknown = new(nameof(Unknown));

    /// <summary>
    /// TODO: define a standard set of filters with known wavelengths and characteristics, and implement a method to create filters from their names. This will allow users to easily apply common filters without needing to specify their properties manually.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static Filter FromName(string name)
    {
        return new Filter(name);
    }
}
