using System;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public class SingleFixedFilterAssembly : FilterAssemblyBase
    {
        private readonly Filter[] _filters;
        public SingleFixedFilterAssembly(Filter filter)
        {
            _filters = new[] { filter };
        }

        public override IReadOnlyCollection<Filter> Filters => _filters;

        public override bool CanChange => false;

        public override void Change(int idx) => throw new InvalidOperationException("Filter change not supported!");
    }
}
