namespace Astap.Lib.Devices.Ascom;

public class AscomFocuserDriver : AscomDeviceDriverBase, IFocuserDriver
{
    public AscomFocuserDriver(AscomDevice device, IExternal external) : base(device, external)
    {
        DeviceConnectedEvent += AscomFocuserDriver_DeviceConnectedEvent;
    }

    private void AscomFocuserDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected && _comObject is { } obj)
        {
            Absolute = obj.Absolute is bool absolute && absolute;
            TempCompAvailable = obj.TempCompAvailable is bool tempCompAvailable && tempCompAvailable;

            try
            {
                MaxIncrement = obj.MaxIncrement is int maxIncrement ? maxIncrement : int.MinValue;
            }
            catch
            {
                MaxIncrement = int.MinValue;
            }
            
            try
            {
                MaxStep = obj.MaxStep is int maxStep ? maxStep : int.MinValue;
            }
            catch
            {
                MaxStep = int.MinValue;
            }

            try
            {
                StepSize = obj.StepSize is double stepSize && !double.IsNaN(stepSize) ? stepSize : double.NaN;
                CanGetStepSize = !double.IsNaN(StepSize);
            }
            catch
            {
                StepSize = double.NaN;
                CanGetStepSize = false;
            }
        }
    }

    public int Position => Connected && Absolute && _comObject?.Position is int pos ? pos : int.MinValue;

    public bool Absolute { get; private set; }

    public bool IsMoving => Connected && _comObject?.IsMoving is bool moving && moving;

    public int MaxIncrement { get; private set; }

    public int MaxStep { get; private set; }

    public double StepSize { get; private set; }

    public bool CanGetStepSize { get; private set; }

    public bool TempComp
    {
        get => Connected && TempCompAvailable && _comObject?.TempComp is bool tempComp && tempComp;

        set
        {
            if (Connected && TempCompAvailable && _comObject is { } obj)
            {
                obj.TempComp = value;
            }
        }
    }

    public bool TempCompAvailable { get; private set; }

    public double Temperature => Connected && _comObject?.Temperature is double temperature ? temperature : double.NaN;

    public bool Move(int position)
    {
        if (Connected
            && (Absolute ? position is >= 0 && position <= MaxStep : position >= -MaxIncrement && position <= MaxIncrement)
            && _comObject is { } obj
        )
        {
            obj.Move(position);

            return true;
        }

        return false;
    }

    public bool Halt()
    {
        if (Connected && _comObject is { } obj)
        {
            if (IsMoving)
            {
                obj.Halt();
            }

            return true;
        }

        return false;
    }
}