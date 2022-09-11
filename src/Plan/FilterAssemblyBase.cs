using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public abstract class FilterAssemblyBase
    {
        public abstract IReadOnlyCollection<Filter> Filters { get; }

        public abstract bool CanChange { get; }

        public abstract void Change(int idx);
    }
}
