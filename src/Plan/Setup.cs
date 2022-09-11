using Astap.Lib.Devices;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public class Setup<T>
        where T : DeviceBase
    {
        private readonly List<Telescope> _telescopes;

        public Setup(MountBase<T> mount, Telescope telescope, params Telescope[] telescopes)
        {
            Mount = mount;
            _telescopes = new(telescopes.Length + 1)
            {
                telescope
            };
            _telescopes.AddRange(telescopes);
        }

        public MountBase<T> Mount { get; }

        public ICollection<Telescope> Telescopes { get { return _telescopes; } }
    }
}
