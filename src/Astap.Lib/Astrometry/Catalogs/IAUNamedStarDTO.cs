using System;

namespace Astap.Lib.Astrometry.Catalogs;

record IAUNamedStarDTO(
    string IAUName,
    string Designation,
    string? ID,
    string Constellation,
    string? WDSComponentId,
    string? WDS_J,
    float? Vmag,
    double RA_J2000, // in degrees 0..360
    double Dec_J2000,
    DateTime ApprovalDate
);
