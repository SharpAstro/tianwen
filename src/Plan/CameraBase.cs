namespace Astap.Lib.Plan
{
    public abstract class CameraBase
    {
        public CameraBase(FilterAssemblyBase filterAssembly)
        {
            FilterAssembly = filterAssembly;
        }

        public abstract bool HasCooler { get; }

        public abstract double PixelSize { get; }

        public FilterAssemblyBase FilterAssembly { get; }
    }
}
