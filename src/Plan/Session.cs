using Astap.Lib.Devices;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public class Session<T>
        where T : DeviceBase
    {
        private readonly List<Target> _targets;
        private int _activeTarget;

        public Session(Setup<T> setup, Target target, params Target[] targets)
        {
            Setup = setup;
            _targets = new(targets.Length + 1)
            {
                target
            };
            _targets.AddRange(targets);

            _activeTarget = -1; // -1 means we have not started imaging yet
        }

        public Setup<T> Setup { get; }

        public bool MoveNext()
        {
            if (_activeTarget >= _targets.Count)
            {
                return false;
            }

            _activeTarget++;

            return true;
        }
    }
}
