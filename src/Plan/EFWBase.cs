using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public abstract class EFWBase : FilterAssemblyBase
    {
        private readonly List<Filter> _filters;

        public EFWBase(Filter filter, params Filter[] filters)
        {
            _filters = new List<Filter>(filters.Length + 1)
            {
                filter
            };
            _filters.AddRange(filters);
        }

        public override bool CanChange => true;
    }
}
