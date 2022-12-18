using Astap.Lib.Devices;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Astap.Lib.Sequencing;

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

    public Target? CurrentTarget => _activeTarget is int active and >= 0 && active < _targets.Count ? _targets[active] : null;

    /// <summary>
    /// Moves to next target in the list, connecting to the mount and guider if required
    /// </summary>
    /// <returns>true iff slew to next target is successful</returns>
    public bool MoveNext()
    {
        var active = Interlocked.Increment(ref _activeTarget);
        if (active >= _targets.Count)
        {
            // this implies that initialisation code has ran once
            if (active > 0)
            {
                TurnOffCooledCameras();
            }
            return false;
        }
        else if (active == 0)
        {
            OpenTelescopeCovers();
            TurnOnCooledCameras();
        }

        return SlewToTarget();
    }

    public bool SlewToTarget()
    {
        var activeTarget = CurrentTarget;

        if (activeTarget is null)
        {
            return false;
        }

        Setup.Mount.Connected = true;

        if (Setup.Mount.Driver.TrackingSpeed != TrackingSpeed.None)
        {
            // TODO convert from J2000 to topo if required
            if (Setup.Mount.Driver.SlewAsync(activeTarget.RA, activeTarget.Dec))
            {
                while (Setup.Mount.Driver.IsSlewing)
                {
                    Thread.Sleep(500);
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        return false;
    }

    public void OpenTelescopeCovers()
    {
        foreach (var telescope in Setup.Telescopes)
        {
            if (telescope.Cover is Cover cover)
            {
                cover.Connected = true;

                cover.Driver.Brightness = 0;
                cover.Driver.Open();
            }
        }
    }

    public void TurnOnCooledCameras()
    {

    }

    public void TurnOffCooledCameras()
    {

    }

}
