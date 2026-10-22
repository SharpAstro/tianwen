using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

public interface IFocuserDriver : IDeviceDriver
{
    ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default);

    ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default);

    ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if the focuser is capable of absolute position; that is, being commanded to a specific step location.
    /// </summary>
    public bool Absolute { get; }

    /// <summary>
    /// Maximum increment size allowed by the focuser; i.e.the maximum number of steps allowed in one move operation.
    /// </summary>
    public int MaxIncrement { get; }

    /// <summary>
    /// MaxStep Maximum step position permitted.
    /// </summary>
    public int MaxStep { get; }

    /// <summary>
    /// The FLOOR of the focuser's position range: the lowest absolute position it accepts, 0 unless the focuser
    /// declares otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a bound on WHERE the focuser can be, not on how far it can move.</b> It is not the smallest
    /// move a focuser can make, which is always a single step; that has no member, and <see cref="MaxIncrement"/>
    /// bounds a move from the other side.</para>
    /// <para>Most focusers count from 0 (ASCOM defines a focuser's position as 0 to <see cref="MaxStep"/>), so the
    /// default keeps every one of them as it was. A focuser whose counter is legitimately negative declares its own
    /// floor: a QHY QFOC keeps a signed counter in non-volatile memory, and read -559 on connect (#1763). Check or
    /// clamp a position with <see cref="FocuserTravelExtensions"/>, never against a literal 0.</para>
    /// </remarks>
    public int MinStep => 0;

    /// <summary>
    /// True if <see cref="StepSize"/> is the known step size of the focuser
    /// </summary>
    public bool CanGetStepSize { get; }

    /// <summary>
    /// Step size(microns) for the focuser.
    /// </summary>
    public double StepSize { get; }

    /// <summary>
    /// True if focuser has temperature compensation available.
    /// </summary>
    public bool TempCompAvailable { get; }

    /// <summary>
    /// Known backlash in steps when moving inward (decreasing position).
    /// Negative means unknown/unmeasured. Zero means no backlash.
    /// </summary>
    public int BacklashStepsIn { get; }

    /// <summary>
    /// Known backlash in steps when moving outward (increasing position).
    /// Negative means unknown/unmeasured. Zero means no backlash.
    /// </summary>
    public int BacklashStepsOut { get; }

    /// <summary>
    /// Moves the focuser by the specified amount or to the specified position depending on the value of the <see cref="Absolute"/> property.
    /// Poll for <see cref="GetIsMovingAsync"/> to see if move is complete.
    /// </summary>
    /// <param name="position">Relative or absolute position to move to.</param>
    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately stop any focuser motion due to a previous move call.
    /// Poll for <see cref="GetIsMovingAsync"/> to see if halt is complete.
    /// </summary>
    public Task BeginHaltAsync(CancellationToken cancellationToken = default);
}
