namespace Astap.Lib.Plan
{
    public class CoverCap : CoverBase
    {
        public CoverCap() : base(CoverProps.None)
        {
            // calls base
        }

        /// <summary>
        /// Return true as we cannot open it manually
        /// </summary>
        public override bool IsOpen => true;
    }
}
