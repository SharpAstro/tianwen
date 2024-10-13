namespace Astap.Lib.Sequencing;

public record GuiderFocuser(Focuser? Focuser = null, Telescope? OAG = null)
{
    public bool IsOAG => OAG != null;

    public bool IsManualFocus => Focuser != null;
}
