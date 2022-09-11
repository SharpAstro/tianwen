using Astap.Lib.Devices;
using System.Collections.Generic;

namespace Astap.Lib.Plan
{
    public class Session<TDevice, TMountDriver, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver>
        where TDevice : DeviceBase
        where TMountDriver : IDeviceDriver
        where TCameraDriver : IDeviceDriver
        where TCoverDriver : IDeviceDriver
        where TFocuserDriver : IDeviceDriver
        where TEFWDriver : IDeviceDriver
    {
        private readonly List<Target> _targets;
        private int _activeTarget;

        public Session(
            Setup<TDevice, TMountDriver, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver> setup,
            Target target,
            params Target[] targets
        )
        {
            Setup = setup;
            _targets = new(targets.Length + 1)
            {
                target
            };
            _targets.AddRange(targets);

            _activeTarget = -1; // -1 means we have not started imaging yet
        }

        public Setup<TDevice, TMountDriver, TCameraDriver, TCoverDriver, TFocuserDriver, TEFWDriver> Setup { get; }

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
