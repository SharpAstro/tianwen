namespace TianWen.Lib.Sequencing;

public record GuiderSetup(Focuser? Focuser = null, OTA? OAG = null)
{
    public bool IsOAG => OAG != null;

    public bool IsManualFocus => Focuser != null;
}
