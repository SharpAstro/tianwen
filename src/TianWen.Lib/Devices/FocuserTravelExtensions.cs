using System;

namespace TianWen.Lib.Devices;

/// <summary>
/// A focuser's travel, <see cref="IFocuserDriver.MinStep"/> to <see cref="IFocuserDriver.MaxStep"/>: the ONE place a
/// position is checked or clamped against it, so no caller writes a 0 of its own as the floor.
/// </summary>
/// <remarks>
/// A <see cref="IFocuserDriver.MaxStep"/> of 0 or less is no upper bound, as every caller already read it before this
/// existed (a focuser that has not said its range).
/// </remarks>
public static class FocuserTravelExtensions
{
    extension(IFocuserDriver focuser)
    {
        /// <summary>True when <paramref name="position"/> lies within the focuser's travel.</summary>
        public bool IsWithinTravel(int position)
            => position >= focuser.MinStep && (focuser.MaxStep <= 0 || position <= focuser.MaxStep);

        /// <summary><paramref name="position"/>, brought inside the focuser's travel.</summary>
        public int ClampToTravel(int position)
        {
            var floored = Math.Max(focuser.MinStep, position);
            return focuser.MaxStep > 0 ? Math.Min(focuser.MaxStep, floored) : floored;
        }

        /// <summary>The travel in words, for a refusal: <c>between -1000000 and 1000000</c>, or <c>from 0</c> with no upper bound.</summary>
        public string TravelInWords => focuser.MaxStep > 0
            ? $"between {focuser.MinStep} and {focuser.MaxStep}"
            : $"from {focuser.MinStep}";
    }
}
