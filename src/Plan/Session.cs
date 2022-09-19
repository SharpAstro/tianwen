using Astap.Lib.Devices;
using System.Collections.Generic;

namespace Astap.Lib.Plan;

public class Session
{
    private readonly List<Target> _targets;
    private int _activeTarget;

    public Session(
        Setup setup,
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

    public Setup Setup { get; }

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
