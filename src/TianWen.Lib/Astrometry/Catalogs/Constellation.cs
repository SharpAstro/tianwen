using System;
using System.Collections.Generic;
using static TianWen.Lib.EnumHelper;

namespace TianWen.Lib.Astrometry.Catalogs;

public enum Constellation : ulong
{
    Andromeda = 'A' << 14 | 'n' << 7 | 'd',
    Antlia = 'A' << 14 | 'n' << 7 | 't',
    Apus = 'A' << 14 | 'p' << 7 | 's',
    Aquarius = 'A' << 14 | 'q' << 7 | 'r',
    Aquila = 'A' << 14 | 'q' << 7 | 'l',
    Ara = 'A' << 14 | 'r' << 7 | 'a',
    Aries = 'A' << 14 | 'r' << 7 | 'i',
    Auriga = 'A' << 14 | 'u' << 7 | 'r',
    Bootes = 'B' << 14 | 'o' << 7 | 'o',
    Caelum = 'C' << 14 | 'a' << 7 | 'e',
    Camelopardalis = 'C' << 14 | 'a' << 7 | 'm',
    Cancer = 'C' << 14 | 'n' << 7 | 'c',
    CanesVenatici = 'C' << 14 | 'V' << 7 | 'n',
    CanisMajor = 'C' << 14 | 'M' << 7 | 'a',
    CanisMinor = 'C' << 14 | 'M' << 7 | 'i',
    Capricornus = 'C' << 14 | 'a' << 7 | 'p',
    Carina = 'C' << 14 | 'a' << 7 | 'r',
    Cassiopeia = 'C' << 14 | 'a' << 7 | 's',
    Centaurus = 'C' << 14 | 'e' << 7 | 'n',
    Cepheus = 'C' << 14 | 'e' << 7 | 'p',
    Cetus = 'C' << 14 | 'e' << 7 | 't',
    Chamaeleon = 'C' << 14 | 'h' << 7 | 'a',
    Circinus = 'C' << 14 | 'i' << 7 | 'r',
    Columba = 'C' << 14 | 'o' << 7 | 'l',
    ComaBerenices = 'C' << 14 | 'o' << 7 | 'm',
    CoronaAustralis = 'C' << 14 | 'r' << 7 | 'A',
    CoronaBorealis = 'C' << 14 | 'r' << 7 | 'B',
    Corvus = 'C' << 14 | 'r' << 7 | 'v',
    Crater = 'C' << 14 | 'r' << 7 | 't',
    Crux = 'C' << 14 | 'r' << 7 | 'u',
    Cygnus = 'C' << 14 | 'y' << 7 | 'g',
    Delphinus = 'D' << 14 | 'e' << 7 | 'l',
    Dorado = 'D' << 14 | 'o' << 7 | 'r',
    Draco = 'D' << 14 | 'r' << 7 | 'a',
    Equuleus = 'E' << 14 | 'q' << 7 | 'u',
    Eridanus = 'E' << 14 | 'r' << 7 | 'i',
    Fornax = 'F' << 14 | 'o' << 7 | 'r',
    Gemini = 'G' << 14 | 'e' << 7 | 'm',
    Grus = 'G' << 14 | 'r' << 7 | 'u',
    Hercules = 'H' << 14 | 'e' << 7 | 'r',
    Horologium = 'H' << 14 | 'o' << 7 | 'r',
    Hydra = 'H' << 14 | 'y' << 7 | 'a',
    Hydrus = 'H' << 14 | 'y' << 7 | 'i',
    Indus = 'I' << 14 | 'n' << 7 | 'd',
    Lacerta = 'L' << 14 | 'a' << 7 | 'c',
    Leo = 'L' << 14 | 'e' << 7 | 'o',
    LeoMinor = 'L' << 14 | 'M' << 7 | 'i',
    Lepus = 'L' << 14 | 'e' << 7 | 'p',
    Libra = 'L' << 14 | 'i' << 7 | 'b',
    Lupus = 'L' << 14 | 'u' << 7 | 'p',
    Lynx = 'L' << 14 | 'y' << 7 | 'n',
    Lyra = 'L' << 14 | 'y' << 7 | 'r',
    Mensa = 'M' << 14 | 'e' << 7 | 'n',
    Microscopium = 'M' << 14 | 'i' << 7 | 'c',
    Monoceros = 'M' << 14 | 'o' << 7 | 'n',
    Musca = 'M' << 14 | 'u' << 7 | 's',
    Norma = 'N' << 14 | 'o' << 7 | 'r',
    Octans = 'O' << 14 | 'c' << 7 | 't',
    Ophiuchus = 'O' << 14 | 'p' << 7 | 'h',
    Orion = 'O' << 14 | 'r' << 7 | 'i',
    Pavo = 'P' << 14 | 'a' << 7 | 'v',
    Pegasus = 'P' << 14 | 'e' << 7 | 'g',
    Perseus = 'P' << 14 | 'e' << 7 | 'r',
    Phoenix = 'P' << 14 | 'h' << 7 | 'e',
    Pictor = 'P' << 14 | 'i' << 7 | 'c',
    Pisces = 'P' << 14 | 's' << 7 | 'c',
    PiscisAustrinus = 'P' << 14 | 's' << 7 | 'A',
    Puppis = 'P' << 14 | 'u' << 7 | 'p',
    Pyxis = 'P' << 14 | 'y' << 7 | 'x',
    Reticulum = 'R' << 14 | 'e' << 7 | 't',
    Sagitta = 'S' << 14 | 'g' << 7 | 'e',
    Sagittarius = 'S' << 14 | 'g' << 7 | 'r',
    Scorpius = 'S' << 14 | 'c' << 7 | 'o',
    Sculptor = 'S' << 14 | 'c' << 7 | 'l',
    Scutum = 'S' << 14 | 'c' << 7 | 't',
    Serpens = 'S' << 14 | 'e' << 7 | 'r',
    SerpensCaput = 'S' << 14 | 'e' << 7 | '1',
    SerpensCauda = 'S' << 14 | 'e' << 7 | '2',
    Sextans = 'S' << 14 | 'e' << 7 | 'x',
    Taurus = 'T' << 14 | 'a' << 7 | 'u',
    Telescopium = 'T' << 14 | 'e' << 7 | 'l',
    Triangulum = 'T' << 14 | 'r' << 7 | 'i',
    TriangulumAustrale = 'T' << 14 | 'r' << 7 | 'A',
    Tucana = 'T' << 14 | 'u' << 7 | 'c',
    UrsaMajor = 'U' << 14 | 'M' << 7 | 'a',
    UrsaMinor = 'U' << 14 | 'M' << 7 | 'i',
    Vela = 'V' << 14 | 'e' << 7 | 'l',
    Virgo = 'V' << 14 | 'i' << 7 | 'r',
    Volans = 'V' << 14 | 'o' << 7 | 'l',
    Vulpecula = 'V' << 14 | 'u' << 7 | 'l'
}

public static class ConstellationEx
{
    static CatalogIndex Cat(string name)
        => CatalogUtils.TryGetCleanedUpCatalogName(name, out var cat)
        ? cat
        : throw new ArgumentException($"Cannot convert {name} to catalog index", nameof(name));

    private static readonly Dictionary<Constellation, (string Genetitive, CatalogIndex BrightestStar)> _info = new()
    {
        [Constellation.Andromeda] = ("Andromedae", Cat("HR 15")),
        [Constellation.Antlia] = ("Antliae", Cat("HR 4104")),
        [Constellation.Apus] = ("Apodis", Cat("HR 5470")),
        [Constellation.Aquarius] = ("Aquarii", Cat("HR 8232")),
        [Constellation.Aquila] = ("Aquilae", Cat("HR 7557")),
        [Constellation.Ara] = ("Arae", Cat("HR 6461")),
        [Constellation.Aries] = ("Arietis", Cat("HR 617")),
        [Constellation.Auriga] = ("Aurigae", Cat("HR 1708")),
        [Constellation.Bootes] = ("Bootis", Cat("HR 5340")),
        [Constellation.Caelum] = ("Caeli", Cat("HR 1502")),
        [Constellation.Camelopardalis] = ("Camelopardalis", Cat("HR 1603")),
        [Constellation.Cancer] = ("Cancri", Cat("HR 3249")),
        [Constellation.CanesVenatici] = ("Canum Venaticorum", Cat("HR 4915")),
        [Constellation.CanisMajor] = ("Canis Majoris", Cat("HR 2491")),
        [Constellation.CanisMinor] = ("Canis Minoris", Cat("HR 2943")),
        [Constellation.Capricornus] = ("Capricorni", Cat("HR 8322")),
        [Constellation.Carina] = ("Carinae", Cat("HR 2326")),
        [Constellation.Cassiopeia] = ("Cassiopeiae", Cat("HR 168")),
        [Constellation.Centaurus] = ("Centauri", Cat("HR 5459")),
        [Constellation.Cepheus] = ("Cephei", Cat("HR 8162")),
        [Constellation.Cetus] = ("Ceti", Cat("HR 188")),
        [Constellation.Chamaeleon] = ("Chamaeleontis", Cat("HR 3318")),
        [Constellation.Circinus] = ("Circini", Cat("HR 5463")),
        [Constellation.Columba] = ("Columbae", Cat("HR 1956")),
        [Constellation.ComaBerenices] = ("Comae Berenices", Cat("HR 4983")),
        [Constellation.CoronaAustralis] = ("Coronae Australis", Cat("HR 7254")),
        [Constellation.CoronaBorealis] = ("Coronae Borealis", Cat("HR 5793")),
        [Constellation.Corvus] = ("Corvi", Cat("HR 4662")),
        [Constellation.Crater] = ("Crateris", Cat("HR 4382")),
        [Constellation.Crux] = ("Crucis", Cat("HR 4730")),
        [Constellation.Cygnus] = ("Cygni", Cat("HR 7924")),
        [Constellation.Delphinus] = ("Delphini", Cat("HR 7882")),
        [Constellation.Dorado] = ("Doradus", Cat("HR 1465")),
        [Constellation.Draco] = ("Draconis", Cat("HR 6705")),
        [Constellation.Equuleus] = ("Equulei", Cat("HR 8131")),
        [Constellation.Eridanus] = ("Eridani", Cat("HR 472")),
        [Constellation.Fornax] = ("Fornacis", Cat("HR 963")),
        [Constellation.Gemini] = ("Geminorum", Cat("HR 2990")),
        [Constellation.Grus] = ("Gruis", Cat("HR 8425")),
        [Constellation.Hercules] = ("Herculis", Cat("HR 6148")),
        [Constellation.Horologium] = ("Horologii", Cat("HR 1326")),
        [Constellation.Hydra] = ("Hydrae", Cat("HR 3748")),
        [Constellation.Hydrus] = ("Hydri", Cat("HR 98")),
        [Constellation.Indus] = ("Indi", Cat("HR 7869")),
        [Constellation.Lacerta] = ("Lacertae", Cat("HR 8585")),
        [Constellation.Leo] = ("Leonis", Cat("HR 3982")),
        [Constellation.LeoMinor] = ("Leonis Minoris", Cat("HR 4247")),
        [Constellation.Lepus] = ("Leporis",Cat("HR 1865")),
        [Constellation.Libra] = ("Librae", Cat("HR 5685")),
        [Constellation.Lupus] = ("Lupi", Cat("HR 5469")),
        [Constellation.Lynx] = ("Lyncis", Cat("HR 3705")),
        [Constellation.Lyra] = ("Lyrae", Cat("HR 7001")),
        [Constellation.Mensa] = ("Mensae", Cat("HR 2261")),
        [Constellation.Microscopium] = ("Microscopii", Cat("HR 8039")),
        [Constellation.Monoceros] = ("Monocerotis", Cat("HR 2356")),
        [Constellation.Musca] = ("Muscae", Cat("HR 4798")),
        [Constellation.Norma] = ("Normae", Cat("HR 6072")),
        [Constellation.Octans] = ("Octantis", Cat("HR 8254")),
        [Constellation.Ophiuchus] = ("Ophiuchi", Cat("HR 6556")),
        [Constellation.Orion] = ("Orionis", Cat("HR 1713")),
        [Constellation.Pavo] = ("Pavonis", Cat("HR 7790")),
        [Constellation.Pegasus] = ("Pegasi", Cat("HR 8308")),
        [Constellation.Perseus] = ("Persei", Cat("HR 1017")),
        [Constellation.Phoenix] = ("Phoenicis", Cat("HR 99")),
        [Constellation.Pictor] = ("Pictoris", Cat("HR 2550")),
        [Constellation.Pisces] = ("Piscium", Cat("HR 437")),
        [Constellation.PiscisAustrinus] = ("Piscis Austrini", Cat("HR 8728")),
        [Constellation.Puppis] = ("Puppis", Cat("HR 3165")),
        [Constellation.Pyxis] = ("Pyxidis",Cat("HR 3468")),
        [Constellation.Reticulum] = ("Reticuli", Cat("HR 1336")),
        [Constellation.Sagitta] = ("Sagittae", Cat("HR 7635")),
        [Constellation.Sagittarius] = ("Sagittarii", Cat("HR 6879")),
        [Constellation.Scorpius] = ("Scorpii", Cat("HR 6134")),
        [Constellation.Sculptor] = ("Sculptoris", Cat("HR 280")),
        [Constellation.Scutum] = ("Scuti", Cat("HR 6973")),
        [Constellation.Serpens] = ("Serpentis", Cat("HR 5854")),
        [Constellation.SerpensCaput] = ("Serpentis", Cat("HR 5854")),
        [Constellation.SerpensCauda] = ("Serpentis", Cat("HR 6869")),
        [Constellation.Sextans] = ("Sextantis", Cat("HR 3981")),
        [Constellation.Taurus] = ("Tauri", Cat("HR 1457")),
        [Constellation.Telescopium] = ("Telescopii", Cat("HR 6897")),
        [Constellation.Triangulum] = ("Trianguli", Cat("HR 622")),
        [Constellation.TriangulumAustrale] = ("Trianguli Australis", Cat("HR 6217")),
        [Constellation.Tucana] = ("Tucanae",Cat("HR 8502")),
        [Constellation.UrsaMajor] = ("Ursae Majoris", Cat("HR 4905")),
        [Constellation.UrsaMinor] = ("Ursae Minoris",Cat("HR 424")),
        [Constellation.Vela] = ("Velorum", Cat("HR 3207")),
        [Constellation.Virgo] = ("Virginis", Cat("HR 5056")),
        [Constellation.Volans] = ("Volantis", Cat("HR 3347")),
        [Constellation.Vulpecula] = ("Vulpeculae", Cat("HR 7405"))
    };

    public static string ToGenitive(this Constellation constellation)
        => _info.TryGetValue(constellation, out var info)
            ? info.Genetitive
            : throw new ArgumentException($"Cannot find genitive for constellation {constellation}", nameof(constellation));

    public static CatalogIndex GetBrighestStar(this Constellation constellation)
        => _info.TryGetValue(constellation, out var info)
            ? info.BrightestStar
            : throw new ArgumentException($"Cannot find brightest star for constellation {constellation}", nameof(constellation));

    public static string ToIAUAbbreviation(this Constellation constellation) => EnumValueToAbbreviation((ulong)constellation);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="constellation"/> is part of <paramref name="parent"/>.
    /// A constellation is always contained within itself.
    /// Special case is <see cref="Constellation.Serpens"/>, as it has two parts:
    /// <list type="bullet">
    ///   <item><see cref="Constellation.SerpensCaput"/></item>
    ///   <item><see cref="Constellation.SerpensCauda"/></item>
    /// </list>
    /// </summary>
    /// <param name="constellation"></param>
    /// <param name="parent"></param>
    /// <returns></returns>
    public static bool IsContainedWithin(this Constellation constellation, Constellation parent)
    {
        if (constellation == parent)
        {
            return true;
        }
        else if (parent == Constellation.Serpens)
        {
            return constellation is Constellation.SerpensCaput or Constellation.SerpensCauda;
        }
        return false;
    }

    public static string ToName(this Constellation constellation) => constellation.PascalCaseStringToName();
}