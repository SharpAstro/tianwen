namespace Astap.Lib.Devices;

public interface IFocuserDriver : IDeviceDriver
{
    /// <summary>
    /// True if the focuser is capable of absolute position; that is, being commanded to a specific step location.
    /// </summary>
    public bool Absolute { get; }

    /// <summary>
    /// True if the focuser is currently moving to a new position.False if the focuser is stationary.
    /// </summary>
    public bool IsMoving { get; }

    /// <summary>
    /// Maximum increment size allowed by the focuser; i.e.the maximum number of steps allowed in one move operation.
    /// </summary>
    public int MaxIncrement { get; }

    /// <summary>
    /// MaxStep Maximum step position permitted.
    /// </summary>
    public int MaxStep { get; }

    /// <summary>
    /// Current focuser position, in steps.
    /// If focuser is <see cref="Absolute"/>, will range from 0 to <see cref="MaxStep"/>, if relative or not connected,
    /// -1 is returned. This deviates from ASCOM which throws an exception. Client code should still check for exceptions regardless.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// Step size(microns) for the focuser.
    /// </summary>
    public double StepSize { get; }

    /// <summary>
    /// The state of temperature compensation mode (if available), else always False.
    /// </summary>
    public bool TempComp { get; set; }

    /// <summary>
    /// True if focuser has temperature compensation available.
    /// </summary>
    public bool TempCompAvailable { get; }

    /// <summary>
    /// Current ambient temperature in degrees Celsius as measured by the focuser.
    /// </summary>
    public double Temperature { get; }

    /// <summary>
    /// Moves the focuser by the specified amount or to the specified position depending on the value of the <see cref="Absolute"/> property.
    /// Poll for <see cref="IsMoving"/> to see if move is complete.
    /// Will return false is position is not in range of (negative <see cref="MaxIncrement"/> to positive <see cref="MaxIncrement"/> for relative focusers)
    /// or 0 to <see cref="MaxStep"/> for absolute ones.
    /// </summary>
    /// <param name="position">Relative or absolute position to move to.</param>
    /// <returns>true if focuser started moving.</returns>
    public bool Move(int position);

    /// <summary>
    /// Immediately stop any focuser motion due to a previous <see cref="Move(int)"/> method call.
    /// Poll for <see cref="IsMoving"/> to see if halt is complete.
    /// Will return true if focuser is already halted.
    /// </summary>
    /// <returns>true if focuser started halting.</returns>
    public bool Halt();
}