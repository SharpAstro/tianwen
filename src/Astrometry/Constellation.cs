﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry
{
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
        Lynx = 'L' << 14 | 'y' << 7 | 'x',
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
        PiscisAustrinus = 'P' << 14 | 's' << 7 | 'a',
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
        private static readonly Dictionary<Constellation, (string Genetitive, string BrightestStar)> _info = new()
        {
            [Constellation.Andromeda] = ("Andromedae", "Alpheratz"),
            [Constellation.Antlia] = ("Antliae", "α Antliae"),
            [Constellation.Apus] = ("Apodis", "α Apodis"),
            [Constellation.Aquarius] = ("Aquarii", "Sadalsuud"),
            [Constellation.Aquila] = ("Aquilae", "Altair"),
            [Constellation.Ara] = ("Arae", "β Arae"),
            [Constellation.Aries] = ("Arietis", "Hamal"),
            [Constellation.Auriga] = ("Aurigae", "Capella"),
            [Constellation.Bootes] = ("Bootis", "Arcturus"),
            [Constellation.Caelum] = ("Caeli", "α Caeli"),
            [Constellation.Camelopardalis] = ("Camelopardalis", "β Camelopardalis"),
            [Constellation.Cancer] = ("Cancri", "Tarf"),
            [Constellation.CanesVenatici] = ("Canum Venaticorum", "Cor Caroli"),
            [Constellation.CanisMajor] = ("Canis Majoris", "Sirius"),
            [Constellation.CanisMinor] = ("Canis Minoris", "Procyon"),
            [Constellation.Capricornus] = ("Capricorni", "Deneb Algedi"),
            [Constellation.Carina] = ("Carinae", "Canopus"),
            [Constellation.Cassiopeia] = ("Cassiopeiae", "Schedar"),
            [Constellation.Cepheus] = ("Centauri", "Rigil Kentaurus"),
            [Constellation.Cetus] = ("Cephei", "Alderamin"),
            [Constellation.Chamaeleon] = ("Ceti", "Diphda"),
            [Constellation.Circinus] = ("Chamaeleontis", "α Chamaeleontis"),
            [Constellation.Columba] = ("Circini", "α Circini"),
            [Constellation.ComaBerenices] = ("Columbae", "Phact"),
            [Constellation.CoronaAustralis] = ("Comae Berenices", "β Comae Berenices"),
            [Constellation.CoronaBorealis] = ("Coronae Borealis", "Alphecca"),
            [Constellation.Corvus] = ("Corvi", "Gienah"),
            [Constellation.Crater] = ("Crateris", "δ Crateris"),
            [Constellation.Crux] = ("Crucis", "Acrux"),
            [Constellation.Cygnus] = ("Cygni", "Deneb"),
            [Constellation.Delphinus] = ("Delphini", "Rotanev"),
            [Constellation.Dorado] = ("Doradus", "α Doradus"),
            [Constellation.Draco] = ("Draconis", "Eltanin"),
            [Constellation.Equuleus] = ("Equulei", "Kitalpha"),
            [Constellation.Eridanus] = ("Eridani", "Achernar"),
            [Constellation.Fornax] = ("Fornacis", "Dalim"),
            [Constellation.Gemini] = ("Geminorum", "Pollux"),
            [Constellation.Grus] = ("Gruis", "Alnair"),
            [Constellation.Hercules] = ("Herculis", "Kornephoros"),
            [Constellation.Horologium] = ("Horologii", "α Horologii"),
            [Constellation.Hydra] = ("Hydrae", "Alphard"),
            [Constellation.Hydrus] = ("Hydri", "β Hydri"),
            [Constellation.Indus] = ("Indi", "α Indi"),
            [Constellation.Lacerta] = ("Lacertae", "α Lacertae"),
            [Constellation.Leo] = ("Leonis", "Regulus"),
            [Constellation.LeoMinor] = ("Leonis Minoris", "Praecipua"),
            [Constellation.Lepus] = ("Leporis", "Arneb"),
            [Constellation.Libra] = ("Librae", "Zubeneschamali"),
            [Constellation.Lupus] = ("Lupi", "α Lupi"),
            [Constellation.Lynx] = ("Lyncis", "α Lyncis"),
            [Constellation.Lyra] = ("Lyrae", "Vega"),
            [Constellation.Mensa] = ("Mensae", "α Mensae"),
            [Constellation.Microscopium] = ("Microscopii", "γ Microscopii"),
            [Constellation.Monoceros] = ("Monocerotis", "β Monocerotis"),
            [Constellation.Musca] = ("Muscae", "α Muscae"),
            [Constellation.Norma] = ("Normae", "γ2 Normae"),
            [Constellation.Octans] = ("Octantis", "ν Octantis"),
            [Constellation.Ophiuchus] = ("Ophiuchi", "Rasalhague"),
            [Constellation.Orion] = ("Orionis", "Rigel"),
            [Constellation.Pavo] = ("Pavonis", "Peacock"),
            [Constellation.Pegasus] = ("Pegasi", "Enif"),
            [Constellation.Perseus] = ("Persei", "Mirfak"),
            [Constellation.Phoenix] = ("Phoenicis", "Ankaa"),
            [Constellation.Pictor] = ("Pictoris", "α Pictoris"),
            [Constellation.Pisces] = ("Piscium", "Alpherg"),
            [Constellation.PiscisAustrinus] = ("Piscis Austrini", "Fomalhaut"),
            [Constellation.Puppis] = ("Puppis", "Naos"),
            [Constellation.Pyxis] = ("Pyxidis", "α Pyxidis"),
            [Constellation.Reticulum] = ("Reticuli", "α Reticuli"),
            [Constellation.Sagitta] = ("Sagittae", "γ Sagittae"),
            [Constellation.Sagittarius] = ("Sagittarii", "Kaus Australis"),
            [Constellation.Scorpius] = ("Scorpii", "Antares"),
            [Constellation.Sculptor] = ("Sculptoris", "α Sculptoris"),
            [Constellation.Scutum] = ("Scuti", "α Scuti"),
            [Constellation.Serpens] = ("Serpentis", "Unukalhai"),
            [Constellation.SerpensCaput] = ("Serpentis", "Unukalhai"),
            [Constellation.SerpensCauda] = ("Serpentis", "η Serpentis"),
            [Constellation.Sextans] = ("Sextantis", "α Sextantis"),
            [Constellation.Taurus] = ("Tauri", "Aldebaran"),
            [Constellation.Telescopium] = ("Telescopii", "α Telescopii"),
            [Constellation.Triangulum] = ("Trianguli", "β Trianguli"),
            [Constellation.TriangulumAustrale] = ("Trianguli Australis", "Atria"),
            [Constellation.Tucana] = ("Tucanae", "α Tucanae"),
            [Constellation.UrsaMajor] = ("Ursae Majoris", "Alioth"),
            [Constellation.UrsaMinor] = ("Ursae Minoris", "Polaris"),
            [Constellation.Vela] = ("Velorum", "γ2 Velorum"),
            [Constellation.Virgo] = ("Virginis", "Spica"),
            [Constellation.Volans] = ("Volantis", "β Volantis"),
            [Constellation.Vulpecula] = ("Vulpeculae", "Anser")
        };

        public static string ToGenitive(this Constellation constellation)
            => _info.TryGetValue(constellation, out var info)
                ? info.Genetitive
                : throw new ArgumentException($"Cannot find genitive for constellation {constellation}", nameof(constellation));

        public static string ToIAUAbbreviation(this Constellation constellation) => EnumValueToAbbreviation((ulong)constellation);

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

        static readonly Regex PascalSplitter = new("([A-Z])|([0-9]+)", RegexOptions.Compiled);

        public static string ToName(this Constellation constellation) => PascalSplitter.Replace(constellation.ToString(), " $1$2").TrimStart();
    }
}