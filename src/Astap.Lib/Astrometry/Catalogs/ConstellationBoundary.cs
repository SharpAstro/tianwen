using System;
using System.Runtime.CompilerServices;

namespace Astap.Lib.Astrometry.Catalogs;

public record struct ConstellationBoundary(double LowerRA, double UpperRA, double LowerDec, Constellation Constellation)
{
    /// <summary>
    /// From ftp://cdsarc.u-strasbg.fr/pub/cats/VI/42/data.dat .
    /// This table gives the constellation boundaries.
    /// Each constellation is bounded by lines of constant RA or constant declination,
    /// in the 1875 equinox coordinate system.
    ///
    /// Each line of the table consists of
    /// (1) lower right ascension boundary (hours)
    /// (2) upper right ascension boundary (hours)
    /// (3) lower (southern) declination boundary (degrees)
    /// (4) constellation abbreviation (3 letters)
    /// </summary>
    static readonly ConstellationBoundary[] _table = new ConstellationBoundary[] {
        new ConstellationBoundary(0.0000, 24.0000, 88.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(8.0000, 14.5000, 86.5000, Constellation.UrsaMinor),
        new ConstellationBoundary(21.0000, 23.0000, 86.1667, Constellation.UrsaMinor),
        new ConstellationBoundary(18.0000, 21.0000, 86.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(0.0000, 8.0000, 85.0000, Constellation.Cepheus),
        new ConstellationBoundary(9.1667, 10.6667, 82.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(0.0000, 5.0000, 80.0000, Constellation.Cepheus),
        new ConstellationBoundary(10.6667, 14.5000, 80.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(17.5000, 18.0000, 80.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(20.1667, 21.0000, 80.0000, Constellation.Draco),
        new ConstellationBoundary(0.0000, 3.5083, 77.0000, Constellation.Cepheus),
        new ConstellationBoundary(11.5000, 13.5833, 77.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(16.5333, 17.5000, 75.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(20.1667, 20.6667, 75.0000, Constellation.Cepheus),
        new ConstellationBoundary(7.9667, 9.1667, 73.5000, Constellation.Camelopardalis),
        new ConstellationBoundary(9.1667, 11.3333, 73.5000, Constellation.Draco),
        new ConstellationBoundary(13.0000, 16.5333, 70.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(3.1000, 3.4167, 68.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(20.4167, 20.6667, 67.0000, Constellation.Draco),
        new ConstellationBoundary(11.3333, 12.0000, 66.5000, Constellation.Draco),
        new ConstellationBoundary(0.0000, 0.3333, 66.0000, Constellation.Cepheus),
        new ConstellationBoundary(14.0000, 15.6667, 66.0000, Constellation.UrsaMinor),
        new ConstellationBoundary(23.5833, 24.0000, 66.0000, Constellation.Cepheus),
        new ConstellationBoundary(12.0000, 13.5000, 64.0000, Constellation.Draco),
        new ConstellationBoundary(13.5000, 14.4167, 63.0000, Constellation.Draco),
        new ConstellationBoundary(23.1667, 23.5833, 63.0000, Constellation.Cepheus),
        new ConstellationBoundary(6.1000, 7.0000, 62.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(20.0000, 20.4167, 61.5000, Constellation.Draco),
        new ConstellationBoundary(20.5367, 20.6000, 60.9167, Constellation.Cepheus),
        new ConstellationBoundary(7.0000, 7.9667, 60.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(7.9667, 8.4167, 60.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(19.7667, 20.0000, 59.5000, Constellation.Draco),
        new ConstellationBoundary(20.0000, 20.5367, 59.5000, Constellation.Cepheus),
        new ConstellationBoundary(22.8667, 23.1667, 59.0833, Constellation.Cepheus),
        new ConstellationBoundary(0.0000, 2.4333, 58.5000, Constellation.Cassiopeia),
        new ConstellationBoundary(19.4167, 19.7667, 58.0000, Constellation.Draco),
        new ConstellationBoundary(1.7000, 1.9083, 57.5000, Constellation.Cassiopeia),
        new ConstellationBoundary(2.4333, 3.1000, 57.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(3.1000, 3.1667, 57.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(22.3167, 22.8667, 56.2500, Constellation.Cepheus),
        new ConstellationBoundary(5.0000, 6.1000, 56.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(14.0333, 14.4167, 55.5000, Constellation.UrsaMajor),
        new ConstellationBoundary(14.4167, 19.4167, 55.5000, Constellation.Draco),
        new ConstellationBoundary(3.1667, 3.3333, 55.0000, Constellation.Camelopardalis),
        new ConstellationBoundary(22.1333, 22.3167, 55.0000, Constellation.Cepheus),
        new ConstellationBoundary(20.6000, 21.9667, 54.8333, Constellation.Cepheus),
        new ConstellationBoundary(0.0000, 1.7000, 54.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(6.1000, 6.5000, 54.0000, Constellation.Lynx),
        new ConstellationBoundary(12.0833, 13.5000, 53.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(15.2500, 15.7500, 53.0000, Constellation.Draco),
        new ConstellationBoundary(21.9667, 22.1333, 52.7500, Constellation.Cepheus),
        new ConstellationBoundary(3.3333, 5.0000, 52.5000, Constellation.Camelopardalis),
        new ConstellationBoundary(22.8667, 23.3333, 52.5000, Constellation.Cassiopeia),
        new ConstellationBoundary(15.7500, 17.0000, 51.5000, Constellation.Draco),
        new ConstellationBoundary(2.0417, 2.5167, 50.5000, Constellation.Perseus),
        new ConstellationBoundary(17.0000, 18.2333, 50.5000, Constellation.Draco),
        new ConstellationBoundary(0.0000, 1.3667, 50.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(1.3667, 1.6667, 50.0000, Constellation.Perseus),
        new ConstellationBoundary(6.5000, 6.8000, 50.0000, Constellation.Lynx),
        new ConstellationBoundary(23.3333, 24.0000, 50.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(13.5000, 14.0333, 48.5000, Constellation.UrsaMajor),
        new ConstellationBoundary(0.0000, 1.1167, 48.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(23.5833, 24.0000, 48.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(18.1750, 18.2333, 47.5000, Constellation.Hercules),
        new ConstellationBoundary(18.2333, 19.0833, 47.5000, Constellation.Draco),
        new ConstellationBoundary(19.0833, 19.1667, 47.5000, Constellation.Cygnus),
        new ConstellationBoundary(1.6667, 2.0417, 47.0000, Constellation.Perseus),
        new ConstellationBoundary(8.4167, 9.1667, 47.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(0.1667, 0.8667, 46.0000, Constellation.Cassiopeia),
        new ConstellationBoundary(12.0000, 12.0833, 45.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(6.8000, 7.3667, 44.5000, Constellation.Lynx),
        new ConstellationBoundary(21.9083, 21.9667, 44.0000, Constellation.Cygnus),
        new ConstellationBoundary(21.8750, 21.9083, 43.7500, Constellation.Cygnus),
        new ConstellationBoundary(19.1667, 19.4000, 43.5000, Constellation.Cygnus),
        new ConstellationBoundary(9.1667, 10.1667, 42.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(10.1667, 10.7833, 40.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(15.4333, 15.7500, 40.0000, Constellation.Bootes),
        new ConstellationBoundary(15.7500, 16.3333, 40.0000, Constellation.Hercules),
        new ConstellationBoundary(9.2500, 9.5833, 39.7500, Constellation.Lynx),
        new ConstellationBoundary(0.0000, 2.5167, 36.7500, Constellation.Andromeda),
        new ConstellationBoundary(2.5167, 2.5667, 36.7500, Constellation.Perseus),
        new ConstellationBoundary(19.3583, 19.4000, 36.5000, Constellation.Lyra),
        new ConstellationBoundary(4.5000, 4.6917, 36.0000, Constellation.Perseus),
        new ConstellationBoundary(21.7333, 21.8750, 36.0000, Constellation.Cygnus),
        new ConstellationBoundary(21.8750, 22.0000, 36.0000, Constellation.Lacerta),
        new ConstellationBoundary(6.5333, 7.3667, 35.5000, Constellation.Auriga),
        new ConstellationBoundary(7.3667, 7.7500, 35.5000, Constellation.Lynx),
        new ConstellationBoundary(0.0000, 2.0000, 35.0000, Constellation.Andromeda),
        new ConstellationBoundary(22.0000, 22.8167, 35.0000, Constellation.Lacerta),
        new ConstellationBoundary(22.8167, 22.8667, 34.5000, Constellation.Lacerta),
        new ConstellationBoundary(22.8667, 23.5000, 34.5000, Constellation.Andromeda),
        new ConstellationBoundary(2.5667, 2.7167, 34.0000, Constellation.Perseus),
        new ConstellationBoundary(10.7833, 11.0000, 34.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(12.0000, 12.3333, 34.0000, Constellation.CanesVenatici),
        new ConstellationBoundary(7.7500, 9.2500, 33.5000, Constellation.Lynx),
        new ConstellationBoundary(9.2500, 9.8833, 33.5000, Constellation.LeoMinor),
        new ConstellationBoundary(0.7167, 1.4083, 33.0000, Constellation.Andromeda),
        new ConstellationBoundary(15.1833, 15.4333, 33.0000, Constellation.Bootes),
        new ConstellationBoundary(23.5000, 23.7500, 32.0833, Constellation.Andromeda),
        new ConstellationBoundary(12.3333, 13.2500, 32.0000, Constellation.CanesVenatici),
        new ConstellationBoundary(23.7500, 24.0000, 31.3333, Constellation.Andromeda),
        new ConstellationBoundary(13.9583, 14.0333, 30.7500, Constellation.CanesVenatici),
        new ConstellationBoundary(2.4167, 2.7167, 30.6667, Constellation.Triangulum),
        new ConstellationBoundary(2.7167, 4.5000, 30.6667, Constellation.Perseus),
        new ConstellationBoundary(4.5000, 4.7500, 30.0000, Constellation.Auriga),
        new ConstellationBoundary(18.1750, 19.3583, 30.0000, Constellation.Lyra),
        new ConstellationBoundary(11.0000, 12.0000, 29.0000, Constellation.UrsaMajor),
        new ConstellationBoundary(19.6667, 20.9167, 29.0000, Constellation.Cygnus),
        new ConstellationBoundary(4.7500, 5.8833, 28.5000, Constellation.Auriga),
        new ConstellationBoundary(9.8833, 10.5000, 28.5000, Constellation.LeoMinor),
        new ConstellationBoundary(13.2500, 13.9583, 28.5000, Constellation.CanesVenatici),
        new ConstellationBoundary(0.0000, 0.0667, 28.0000, Constellation.Andromeda),
        new ConstellationBoundary(1.4083, 1.6667, 28.0000, Constellation.Triangulum),
        new ConstellationBoundary(5.8833, 6.5333, 28.0000, Constellation.Auriga),
        new ConstellationBoundary(7.8833, 8.0000, 28.0000, Constellation.Gemini),
        new ConstellationBoundary(20.9167, 21.7333, 28.0000, Constellation.Cygnus),
        new ConstellationBoundary(19.2583, 19.6667, 27.5000, Constellation.Cygnus),
        new ConstellationBoundary(1.9167, 2.4167, 27.2500, Constellation.Triangulum),
        new ConstellationBoundary(16.1667, 16.3333, 27.0000, Constellation.CoronaBorealis),
        new ConstellationBoundary(15.0833, 15.1833, 26.0000, Constellation.Bootes),
        new ConstellationBoundary(15.1833, 16.1667, 26.0000, Constellation.CoronaBorealis),
        new ConstellationBoundary(18.3667, 18.8667, 26.0000, Constellation.Lyra),
        new ConstellationBoundary(10.7500, 11.0000, 25.5000, Constellation.LeoMinor),
        new ConstellationBoundary(18.8667, 19.2583, 25.5000, Constellation.Lyra),
        new ConstellationBoundary(1.6667, 1.9167, 25.0000, Constellation.Triangulum),
        new ConstellationBoundary(0.7167, 0.8500, 23.7500, Constellation.Pisces),
        new ConstellationBoundary(10.5000, 10.7500, 23.5000, Constellation.LeoMinor),
        new ConstellationBoundary(21.2500, 21.4167, 23.5000, Constellation.Vulpecula),
        new ConstellationBoundary(5.7000, 5.8833, 22.8333, Constellation.Taurus),
        new ConstellationBoundary(0.0667, 0.1417, 22.0000, Constellation.Andromeda),
        new ConstellationBoundary(15.9167, 16.0333, 22.0000, Constellation.Serpens),
        new ConstellationBoundary(5.8833, 6.2167, 21.5000, Constellation.Gemini),
        new ConstellationBoundary(19.8333, 20.2500, 21.2500, Constellation.Vulpecula),
        new ConstellationBoundary(18.8667, 19.2500, 21.0833, Constellation.Vulpecula),
        new ConstellationBoundary(0.1417, 0.8500, 21.0000, Constellation.Andromeda),
        new ConstellationBoundary(20.2500, 20.5667, 20.5000, Constellation.Vulpecula),
        new ConstellationBoundary(7.8083, 7.8833, 20.0000, Constellation.Gemini),
        new ConstellationBoundary(20.5667, 21.2500, 19.5000, Constellation.Vulpecula),
        new ConstellationBoundary(19.2500, 19.8333, 19.1667, Constellation.Vulpecula),
        new ConstellationBoundary(3.2833, 3.3667, 19.0000, Constellation.Aries),
        new ConstellationBoundary(18.8667, 19.0000, 18.5000, Constellation.Sagitta),
        new ConstellationBoundary(5.7000, 5.7667, 18.0000, Constellation.Orion),
        new ConstellationBoundary(6.2167, 6.3083, 17.5000, Constellation.Gemini),
        new ConstellationBoundary(19.0000, 19.8333, 16.1667, Constellation.Sagitta),
        new ConstellationBoundary(4.9667, 5.3333, 16.0000, Constellation.Taurus),
        new ConstellationBoundary(15.9167, 16.0833, 16.0000, Constellation.Hercules),
        new ConstellationBoundary(19.8333, 20.2500, 15.7500, Constellation.Sagitta),
        new ConstellationBoundary(4.6167, 4.9667, 15.5000, Constellation.Taurus),
        new ConstellationBoundary(5.3333, 5.6000, 15.5000, Constellation.Taurus),
        new ConstellationBoundary(12.8333, 13.5000, 15.0000, Constellation.ComaBerenices),
        new ConstellationBoundary(17.2500, 18.2500, 14.3333, Constellation.Hercules),
        new ConstellationBoundary(11.8667, 12.8333, 14.0000, Constellation.ComaBerenices),
        new ConstellationBoundary(7.5000, 7.8083, 13.5000, Constellation.Gemini),
        new ConstellationBoundary(16.7500, 17.2500, 12.8333, Constellation.Hercules),
        new ConstellationBoundary(0.0000, 0.1417, 12.5000, Constellation.Pegasus),
        new ConstellationBoundary(5.6000, 5.7667, 12.5000, Constellation.Taurus),
        new ConstellationBoundary(7.0000, 7.5000, 12.5000, Constellation.Gemini),
        new ConstellationBoundary(21.1167, 21.3333, 12.5000, Constellation.Pegasus),
        new ConstellationBoundary(6.3083, 6.9333, 12.0000, Constellation.Gemini),
        new ConstellationBoundary(18.2500, 18.8667, 12.0000, Constellation.Hercules),
        new ConstellationBoundary(20.8750, 21.0500, 11.8333, Constellation.Delphinus),
        new ConstellationBoundary(21.0500, 21.1167, 11.8333, Constellation.Pegasus),
        new ConstellationBoundary(11.5167, 11.8667, 11.0000, Constellation.Leo),
        new ConstellationBoundary(6.2417, 6.3083, 10.0000, Constellation.Orion),
        new ConstellationBoundary(6.9333, 7.0000, 10.0000, Constellation.Gemini),
        new ConstellationBoundary(7.8083, 7.9250, 10.0000, Constellation.Cancer),
        new ConstellationBoundary(23.8333, 24.0000, 10.0000, Constellation.Pegasus),
        new ConstellationBoundary(1.6667, 3.2833, 9.9167, Constellation.Aries),
        new ConstellationBoundary(20.1417, 20.3000, 8.5000, Constellation.Delphinus),
        new ConstellationBoundary(13.5000, 15.0833, 8.0000, Constellation.Bootes),
        new ConstellationBoundary(22.7500, 23.8333, 7.5000, Constellation.Pegasus),
        new ConstellationBoundary(7.9250, 9.2500, 7.0000, Constellation.Cancer),
        new ConstellationBoundary(9.2500, 10.7500, 7.0000, Constellation.Leo),
        new ConstellationBoundary(18.2500, 18.6622, 6.2500, Constellation.Ophiuchus),
        new ConstellationBoundary(18.6622, 18.8667, 6.2500, Constellation.Aquila),
        new ConstellationBoundary(20.8333, 20.8750, 6.0000, Constellation.Delphinus),
        new ConstellationBoundary(7.0000, 7.0167, 5.5000, Constellation.CanisMinor),
        new ConstellationBoundary(18.2500, 18.4250, 4.5000, Constellation.Serpens),
        new ConstellationBoundary(16.0833, 16.7500, 4.0000, Constellation.Hercules),
        new ConstellationBoundary(18.2500, 18.4250, 3.0000, Constellation.Ophiuchus),
        new ConstellationBoundary(21.4667, 21.6667, 2.7500, Constellation.Pegasus),
        new ConstellationBoundary(0.0000, 2.0000, 2.0000, Constellation.Pisces),
        new ConstellationBoundary(18.5833, 18.8667, 2.0000, Constellation.Serpens),
        new ConstellationBoundary(20.3000, 20.8333, 2.0000, Constellation.Delphinus),
        new ConstellationBoundary(20.8333, 21.3333, 2.0000, Constellation.Equuleus),
        new ConstellationBoundary(21.3333, 21.4667, 2.0000, Constellation.Pegasus),
        new ConstellationBoundary(22.0000, 22.7500, 2.0000, Constellation.Pegasus),
        new ConstellationBoundary(21.6667, 22.0000, 1.7500, Constellation.Pegasus),
        new ConstellationBoundary(7.0167, 7.2000, 1.5000, Constellation.CanisMinor),
        new ConstellationBoundary(3.5833, 4.6167, 0.0000, Constellation.Taurus),
        new ConstellationBoundary(4.6167, 4.6667, 0.0000, Constellation.Orion),
        new ConstellationBoundary(7.2000, 8.0833, 0.0000, Constellation.CanisMinor),
        new ConstellationBoundary(14.6667, 15.0833, 0.0000, Constellation.Virgo),
        new ConstellationBoundary(17.8333, 18.2500, 0.0000, Constellation.Ophiuchus),
        new ConstellationBoundary(2.6500, 3.2833, -1.7500, Constellation.Cetus),
        new ConstellationBoundary(3.2833, 3.5833, -1.7500, Constellation.Taurus),
        new ConstellationBoundary(15.0833, 16.2667, -3.2500, Constellation.Serpens),
        new ConstellationBoundary(4.6667, 5.0833, -4.0000, Constellation.Orion),
        new ConstellationBoundary(5.8333, 6.2417, -4.0000, Constellation.Orion),
        new ConstellationBoundary(17.8333, 17.9667, -4.0000, Constellation.Serpens),
        new ConstellationBoundary(18.2500, 18.5833, -4.0000, Constellation.Serpens),
        new ConstellationBoundary(18.5833, 18.8667, -4.0000, Constellation.Aquila),
        new ConstellationBoundary(22.7500, 23.8333, -4.0000, Constellation.Pisces),
        new ConstellationBoundary(10.7500, 11.5167, -6.0000, Constellation.Leo),
        new ConstellationBoundary(11.5167, 11.8333, -6.0000, Constellation.Virgo),
        new ConstellationBoundary(0.0000, 0.3333, -7.0000, Constellation.Pisces),
        new ConstellationBoundary(23.8333, 24.0000, -7.0000, Constellation.Pisces),
        new ConstellationBoundary(14.2500, 14.6667, -8.0000, Constellation.Virgo),
        new ConstellationBoundary(15.9167, 16.2667, -8.0000, Constellation.Ophiuchus),
        new ConstellationBoundary(20.0000, 20.5333, -9.0000, Constellation.Aquila),
        new ConstellationBoundary(21.3333, 21.8667, -9.0000, Constellation.Aquarius),
        new ConstellationBoundary(17.1667, 17.9667, -10.0000, Constellation.Ophiuchus),
        new ConstellationBoundary(5.8333, 8.0833, -11.0000, Constellation.Monoceros),
        new ConstellationBoundary(4.9167, 5.0833, -11.0000, Constellation.Eridanus),
        new ConstellationBoundary(5.0833, 5.8333, -11.0000, Constellation.Orion),
        new ConstellationBoundary(8.0833, 8.3667, -11.0000, Constellation.Hydra),
        new ConstellationBoundary(9.5833, 10.7500, -11.0000, Constellation.Sextans),
        new ConstellationBoundary(11.8333, 12.8333, -11.0000, Constellation.Virgo),
        new ConstellationBoundary(17.5833, 17.6667, -11.6667, Constellation.Ophiuchus),
        new ConstellationBoundary(18.8667, 20.0000, -12.0333, Constellation.Aquila),
        new ConstellationBoundary(4.8333, 4.9167, -14.5000, Constellation.Eridanus),
        new ConstellationBoundary(20.5333, 21.3333, -15.0000, Constellation.Aquarius),
        new ConstellationBoundary(17.1667, 18.2500, -16.0000, Constellation.Serpens),
        new ConstellationBoundary(18.2500, 18.8667, -16.0000, Constellation.Scutum),
        new ConstellationBoundary(8.3667, 8.5833, -17.0000, Constellation.Hydra),
        new ConstellationBoundary(16.2667, 16.3750, -18.2500, Constellation.Ophiuchus),
        new ConstellationBoundary(8.5833, 9.0833, -19.0000, Constellation.Hydra),
        new ConstellationBoundary(10.7500, 10.8333, -19.0000, Constellation.Crater),
        new ConstellationBoundary(16.2667, 16.3750, -19.2500, Constellation.Scorpius),
        new ConstellationBoundary(15.6667, 15.9167, -20.0000, Constellation.Libra),
        new ConstellationBoundary(12.5833, 12.8333, -22.0000, Constellation.Corvus),
        new ConstellationBoundary(12.8333, 14.2500, -22.0000, Constellation.Virgo),
        new ConstellationBoundary(9.0833, 9.7500, -24.0000, Constellation.Hydra),
        new ConstellationBoundary(1.6667, 2.6500, -24.3833, Constellation.Cetus),
        new ConstellationBoundary(2.6500, 3.7500, -24.3833, Constellation.Eridanus),
        new ConstellationBoundary(10.8333, 11.8333, -24.5000, Constellation.Crater),
        new ConstellationBoundary(11.8333, 12.5833, -24.5000, Constellation.Corvus),
        new ConstellationBoundary(14.2500, 14.9167, -24.5000, Constellation.Libra),
        new ConstellationBoundary(16.2667, 16.7500, -24.5833, Constellation.Ophiuchus),
        new ConstellationBoundary(0.0000, 1.6667, -25.5000, Constellation.Cetus),
        new ConstellationBoundary(21.3333, 21.8667, -25.5000, Constellation.Capricornus),
        new ConstellationBoundary(21.8667, 23.8333, -25.5000, Constellation.Aquarius),
        new ConstellationBoundary(23.8333, 24.0000, -25.5000, Constellation.Cetus),
        new ConstellationBoundary(9.7500, 10.2500, -26.5000, Constellation.Hydra),
        new ConstellationBoundary(4.7000, 4.8333, -27.2500, Constellation.Eridanus),
        new ConstellationBoundary(4.8333, 6.1167, -27.2500, Constellation.Lepus),
        new ConstellationBoundary(20.0000, 21.3333, -28.0000, Constellation.Capricornus),
        new ConstellationBoundary(10.2500, 10.5833, -29.1667, Constellation.Hydra),
        new ConstellationBoundary(12.5833, 14.9167, -29.5000, Constellation.Hydra),
        new ConstellationBoundary(14.9167, 15.6667, -29.5000, Constellation.Libra),
        new ConstellationBoundary(15.6667, 16.0000, -29.5000, Constellation.Scorpius),
        new ConstellationBoundary(4.5833, 4.7000, -30.0000, Constellation.Eridanus),
        new ConstellationBoundary(16.7500, 17.6000, -30.0000, Constellation.Ophiuchus),
        new ConstellationBoundary(17.6000, 17.8333, -30.0000, Constellation.Sagittarius),
        new ConstellationBoundary(10.5833, 10.8333, -31.1667, Constellation.Hydra),
        new ConstellationBoundary(6.1167, 7.3667, -33.0000, Constellation.CanisMajor),
        new ConstellationBoundary(12.2500, 12.5833, -33.0000, Constellation.Hydra),
        new ConstellationBoundary(10.8333, 12.2500, -35.0000, Constellation.Hydra),
        new ConstellationBoundary(3.5000, 3.7500, -36.0000, Constellation.Fornax),
        new ConstellationBoundary(8.3667, 9.3667, -36.7500, Constellation.Pyxis),
        new ConstellationBoundary(4.2667, 4.5833, -37.0000, Constellation.Eridanus),
        new ConstellationBoundary(17.8333, 19.1667, -37.0000, Constellation.Sagittarius),
        new ConstellationBoundary(21.3333, 23.0000, -37.0000, Constellation.PiscisAustrinus),
        new ConstellationBoundary(23.0000, 23.3333, -37.0000, Constellation.Sculptor),
        new ConstellationBoundary(3.0000, 3.5000, -39.5833, Constellation.Fornax),
        new ConstellationBoundary(9.3667, 11.0000, -39.7500, Constellation.Antlia),
        new ConstellationBoundary(0.0000, 1.6667, -40.0000, Constellation.Sculptor),
        new ConstellationBoundary(1.6667, 3.0000, -40.0000, Constellation.Fornax),
        new ConstellationBoundary(3.8667, 4.2667, -40.0000, Constellation.Eridanus),
        new ConstellationBoundary(23.3333, 24.0000, -40.0000, Constellation.Sculptor),
        new ConstellationBoundary(14.1667, 14.9167, -42.0000, Constellation.Centaurus),
        new ConstellationBoundary(15.6667, 16.0000, -42.0000, Constellation.Lupus),
        new ConstellationBoundary(16.0000, 16.4208, -42.0000, Constellation.Scorpius),
        new ConstellationBoundary(4.8333, 5.0000, -43.0000, Constellation.Caelum),
        new ConstellationBoundary(5.0000, 6.5833, -43.0000, Constellation.Columba),
        new ConstellationBoundary(8.0000, 8.3667, -43.0000, Constellation.Puppis),
        new ConstellationBoundary(3.4167, 3.8667, -44.0000, Constellation.Eridanus),
        new ConstellationBoundary(16.4208, 17.8333, -45.5000, Constellation.Scorpius),
        new ConstellationBoundary(17.8333, 19.1667, -45.5000, Constellation.CoronaAustralis),
        new ConstellationBoundary(19.1667, 20.3333, -45.5000, Constellation.Sagittarius),
        new ConstellationBoundary(20.3333, 21.3333, -45.5000, Constellation.Microscopium),
        new ConstellationBoundary(3.0000, 3.4167, -46.0000, Constellation.Eridanus),
        new ConstellationBoundary(4.5000, 4.8333, -46.5000, Constellation.Caelum),
        new ConstellationBoundary(15.3333, 15.6667, -48.0000, Constellation.Lupus),
        new ConstellationBoundary(0.0000, 2.3333, -48.1667, Constellation.Phoenix),
        new ConstellationBoundary(2.6667, 3.0000, -49.0000, Constellation.Eridanus),
        new ConstellationBoundary(4.0833, 4.2667, -49.0000, Constellation.Horologium),
        new ConstellationBoundary(4.2667, 4.5000, -49.0000, Constellation.Caelum),
        new ConstellationBoundary(21.3333, 22.0000, -50.0000, Constellation.Grus),
        new ConstellationBoundary(6.0000, 8.0000, -50.7500, Constellation.Puppis),
        new ConstellationBoundary(8.0000, 8.1667, -50.7500, Constellation.Vela),
        new ConstellationBoundary(2.4167, 2.6667, -51.0000, Constellation.Eridanus),
        new ConstellationBoundary(3.8333, 4.0833, -51.0000, Constellation.Horologium),
        new ConstellationBoundary(0.0000, 1.8333, -51.5000, Constellation.Phoenix),
        new ConstellationBoundary(6.0000, 6.1667, -52.5000, Constellation.Carina),
        new ConstellationBoundary(8.1667, 8.4500, -53.0000, Constellation.Vela),
        new ConstellationBoundary(3.5000, 3.8333, -53.1667, Constellation.Horologium),
        new ConstellationBoundary(3.8333, 4.0000, -53.1667, Constellation.Dorado),
        new ConstellationBoundary(0.0000, 1.5833, -53.5000, Constellation.Phoenix),
        new ConstellationBoundary(2.1667, 2.4167, -54.0000, Constellation.Eridanus),
        new ConstellationBoundary(4.5000, 5.0000, -54.0000, Constellation.Pictor),
        new ConstellationBoundary(15.0500, 15.3333, -54.0000, Constellation.Lupus),
        new ConstellationBoundary(8.4500, 8.8333, -54.5000, Constellation.Vela),
        new ConstellationBoundary(6.1667, 6.5000, -55.0000, Constellation.Carina),
        new ConstellationBoundary(11.8333, 12.8333, -55.0000, Constellation.Centaurus),
        new ConstellationBoundary(14.1667, 15.0500, -55.0000, Constellation.Lupus),
        new ConstellationBoundary(15.0500, 15.3333, -55.0000, Constellation.Norma),
        new ConstellationBoundary(4.0000, 4.3333, -56.5000, Constellation.Dorado),
        new ConstellationBoundary(8.8333, 11.0000, -56.5000, Constellation.Vela),
        new ConstellationBoundary(11.0000, 11.2500, -56.5000, Constellation.Centaurus),
        new ConstellationBoundary(17.5000, 18.0000, -57.0000, Constellation.Ara),
        new ConstellationBoundary(18.0000, 20.3333, -57.0000, Constellation.Telescopium),
        new ConstellationBoundary(22.0000, 23.3333, -57.0000, Constellation.Grus),
        new ConstellationBoundary(3.2000, 3.5000, -57.5000, Constellation.Horologium),
        new ConstellationBoundary(5.0000, 5.5000, -57.5000, Constellation.Pictor),
        new ConstellationBoundary(6.5000, 6.8333, -58.0000, Constellation.Carina),
        new ConstellationBoundary(0.0000, 1.3333, -58.5000, Constellation.Phoenix),
        new ConstellationBoundary(1.3333, 2.1667, -58.5000, Constellation.Eridanus),
        new ConstellationBoundary(23.3333, 24.0000, -58.5000, Constellation.Phoenix),
        new ConstellationBoundary(4.3333, 4.5833, -59.0000, Constellation.Dorado),
        new ConstellationBoundary(15.3333, 16.4208, -60.0000, Constellation.Norma),
        new ConstellationBoundary(20.3333, 21.3333, -60.0000, Constellation.Indus),
        new ConstellationBoundary(5.5000, 6.0000, -61.0000, Constellation.Pictor),
        new ConstellationBoundary(15.1667, 15.3333, -61.0000, Constellation.Circinus),
        new ConstellationBoundary(16.4208, 16.5833, -61.0000, Constellation.Ara),
        new ConstellationBoundary(14.9167, 15.1667, -63.5833, Constellation.Circinus),
        new ConstellationBoundary(16.5833, 16.7500, -63.5833, Constellation.Ara),
        new ConstellationBoundary(6.0000, 6.8333, -64.0000, Constellation.Pictor),
        new ConstellationBoundary(6.8333, 9.0333, -64.0000, Constellation.Carina),
        new ConstellationBoundary(11.2500, 11.8333, -64.0000, Constellation.Centaurus),
        new ConstellationBoundary(11.8333, 12.8333, -64.0000, Constellation.Crux),
        new ConstellationBoundary(12.8333, 14.5333, -64.0000, Constellation.Centaurus),
        new ConstellationBoundary(13.5000, 13.6667, -65.0000, Constellation.Circinus),
        new ConstellationBoundary(16.7500, 16.8333, -65.0000, Constellation.Ara),
        new ConstellationBoundary(2.1667, 3.2000, -67.5000, Constellation.Horologium),
        new ConstellationBoundary(3.2000, 4.5833, -67.5000, Constellation.Reticulum),
        new ConstellationBoundary(14.7500, 14.9167, -67.5000, Constellation.Circinus),
        new ConstellationBoundary(16.8333, 17.5000, -67.5000, Constellation.Ara),
        new ConstellationBoundary(17.5000, 18.0000, -67.5000, Constellation.Pavo),
        new ConstellationBoundary(22.0000, 23.3333, -67.5000, Constellation.Tucana),
        new ConstellationBoundary(4.5833, 6.5833, -70.0000, Constellation.Dorado),
        new ConstellationBoundary(13.6667, 14.7500, -70.0000, Constellation.Circinus),
        new ConstellationBoundary(14.7500, 17.0000, -70.0000, Constellation.TriangulumAustrale),
        new ConstellationBoundary(0.0000, 1.3333, -75.0000, Constellation.Tucana),
        new ConstellationBoundary(3.5000, 4.5833, -75.0000, Constellation.Hydrus),
        new ConstellationBoundary(6.5833, 9.0333, -75.0000, Constellation.Volans),
        new ConstellationBoundary(9.0333, 11.2500, -75.0000, Constellation.Carina),
        new ConstellationBoundary(11.2500, 13.6667, -75.0000, Constellation.Musca),
        new ConstellationBoundary(18.0000, 21.3333, -75.0000, Constellation.Pavo),
        new ConstellationBoundary(21.3333, 23.3333, -75.0000, Constellation.Indus),
        new ConstellationBoundary(23.3333, 24.0000, -75.0000, Constellation.Tucana),
        new ConstellationBoundary(0.7500, 1.3333, -76.0000, Constellation.Tucana),
        new ConstellationBoundary(0.0000, 3.5000, -82.5000, Constellation.Hydrus),
        new ConstellationBoundary(7.6667, 13.6667, -82.5000, Constellation.Chamaeleon),
        new ConstellationBoundary(13.6667, 18.0000, -82.5000, Constellation.Apus),
        new ConstellationBoundary(3.5000, 7.6667, -85.0000, Constellation.Mensa),
        new ConstellationBoundary(0.0000, 24.0000, -90.0000, Constellation.Octans)
    };

    private static readonly int[] _decLookupTable = new int[90 + 90 + 1];

    static ConstellationBoundary()
    {
        Array.Fill(_decLookupTable, -1);
        for (var index = 0; index < _table.Length; index++)
        {
            var entry = _table[index];
            var lookupIndex = DecToLookupIndex(entry.LowerDec);
            var tableIndex = _decLookupTable[lookupIndex];
            if (tableIndex < 0)
            {
                _decLookupTable[lookupIndex] = index;
            }
        }

        var fillIndex = 0;
        for (var decIndex = 0; decIndex < _decLookupTable.Length; decIndex++)
        {
            var tableIndex = _decLookupTable[decIndex];

            if (tableIndex >= 0)
            {
                fillIndex = tableIndex;
            }
            else
            {
                _decLookupTable[decIndex] = fillIndex;
            }
        }
    }

    public static bool IsBordering(Constellation borderingConstellation, double ra, double dec, double epoch = 2000.0)
    {
        var ra_s = CoordinateUtils.ConditionRA(ra - 0.001);
        var ra_l = CoordinateUtils.ConditionRA(ra + 0.001);

        return TryFindConstellation(ra_s, dec, epoch, out var const_s)
            && TryFindConstellation(ra_l, dec, epoch, out var const_l)
            && (borderingConstellation.IsContainedWithin(const_s) || borderingConstellation.IsContainedWithin(const_l));
    }

    public static bool TryFindConstellation(double ra, double dec, out Constellation constellation) => TryFindConstellation(ra, dec, 2000.0, out constellation);

    public static bool TryFindConstellation(double ra, double dec, double epoch, out Constellation constellation)
    {
        if (double.IsNaN(dec) || double.IsNaN(ra) || double.IsNaN(epoch))
        {
            constellation = (Constellation)ulong.MaxValue;
            return false;
        }

        var (ra1875, dec1875) = CoordinateUtils.Precess(ra, dec, epoch, 1875.0);
        return TryFindConstellationInEpoch1875Boundaries(ra1875, dec1875, out constellation);
    }

    private static bool TryFindConstellationInEpoch1875Boundaries(double ra, double dec, out Constellation constellation)
    {
        var startIdx = DecToTableIndex(dec);
        for (var i = startIdx; i < _table.Length; i++)
        {
            var entry = _table[i];
            if (dec < entry.LowerDec || ra < entry.LowerRA || ra >= entry.UpperRA)
            {
                continue;
            }

            constellation = entry.Constellation;
            return true;
        }
        constellation = (Constellation)ulong.MaxValue;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int DecToLookupIndex(double dec) => (int)Math.Ceiling(dec + 90);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int DecToTableIndex(double dec) => _decLookupTable[DecToLookupIndex(dec)];
}