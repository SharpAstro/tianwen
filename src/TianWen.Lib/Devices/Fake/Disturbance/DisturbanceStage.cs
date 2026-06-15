namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// Where in the optical chain a disturbance injects, ordered upstream (sky) to
    /// downstream (sensor). The order matters: a <see cref="CorrectionActuator"/> inserted at
    /// a given stage moves everything from that stage downstream to the sensor, so it can null
    /// a disturbance at its own stage or any stage downstream of it -- but nothing upstream.
    /// </summary>
    internal enum DisturbanceStage
    {
        /// <summary>Air path, upstream of the mount. Only a sensor-side actuator (tip-tilt / AO)
        /// reaches it; a mount pulse cannot. Source of atmospheric seeing.</summary>
        Atmosphere = 0,

        /// <summary>RA/Dec axes and bearings: polar misalignment, cone error. A mount pulse
        /// acts here, so it can null this and everything downstream.</summary>
        MountAxis = 1,

        /// <summary>Worm and gear train: periodic error, gear noise, backlash.</summary>
        Drivetrain = 2,

        /// <summary>Optical tube and its attachments: flexure, cable snag, wind loading.</summary>
        OpticalTube = 3,
    }
}
