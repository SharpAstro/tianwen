namespace Astap.Lib.Plan
{
    public abstract class CoverBase
    {
        public CoverBase(CoverProps coverProps)
        {

        }

        public CoverProps CoverProps { get; }

        public abstract bool IsOpen { get; }
    }
}
