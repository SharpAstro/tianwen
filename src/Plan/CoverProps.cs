using System;

namespace Astap.Lib.Plan
{
    [Flags]
    public enum CoverProps
    {
        None = 0,
        HasMotor = 1,
        HasFlatPanel = 1 << 1,
    }
}
