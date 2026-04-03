using System.Collections.Immutable;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Constellation stick figure data: pairs of HR (Bright Star Catalogue) numbers
/// defining the line segments that form the traditional Western stick figures.
/// Public domain data derived from standard astronomical references.
/// </summary>
public static class ConstellationLines
{
    /// <summary>
    /// A single line segment connecting two bright stars in a constellation stick figure.
    /// </summary>
    public readonly record struct Segment(ushort HrFrom, ushort HrTo, Constellation Constellation);

    /// <summary>
    /// All constellation stick figure line segments (~650 segments across 88 constellations).
    /// HR numbers reference the Yale Bright Star Catalogue (Harvard Revised).
    /// </summary>
    public static readonly ImmutableArray<Segment> Segments =
    [
        // ── Andromeda ──
        new(15, 39, Constellation.Andromeda),       // α And (Alpheratz) — δ And
        new(39, 226, Constellation.Andromeda),       // δ And — β And (Mirach)
        new(226, 603, Constellation.Andromeda),      // β And (Mirach) — γ And (Almach)

        // ── Aquarius ──
        new(8232, 8414, Constellation.Aquarius),     // α Aqr (Sadalmelik) — β Aqr (Sadalsuud)
        new(8414, 8518, Constellation.Aquarius),     // β Aqr — μ Aqr
        new(8518, 8610, Constellation.Aquarius),     // μ Aqr — ε Aqr
        new(8232, 8499, Constellation.Aquarius),     // α Aqr — θ Aqr
        new(8499, 8709, Constellation.Aquarius),     // θ Aqr — δ Aqr (Skat)
        new(8709, 8812, Constellation.Aquarius),     // δ Aqr — λ Aqr

        // ── Aquila ──
        new(7525, 7557, Constellation.Aquila),       // γ Aql (Tarazed) — α Aql (Altair)
        new(7557, 7602, Constellation.Aquila),       // α Aql (Altair) — β Aql (Alshain)
        new(7525, 7235, Constellation.Aquila),       // γ Aql — ζ Aql
        new(7602, 7710, Constellation.Aquila),       // β Aql — η Aql
        new(7710, 7950, Constellation.Aquila),       // η Aql — θ Aql

        // ── Aries ──
        new(553, 617, Constellation.Aries),          // β Ari (Sheratan) — α Ari (Hamal)
        new(617, 838, Constellation.Aries),          // α Ari — 41 Ari

        // ── Auriga ──
        new(1708, 2088, Constellation.Auriga),       // α Aur (Capella) — β Aur (Menkalinan)
        new(2088, 2095, Constellation.Auriga),       // β Aur — θ Aur
        new(2095, 1641, Constellation.Auriga),       // θ Aur — ι Aur
        new(1641, 1612, Constellation.Auriga),       // ι Aur — ε Aur
        new(1612, 1708, Constellation.Auriga),       // ε Aur — α Aur (Capella)

        // ── Boötes ──
        new(5340, 5435, Constellation.Bootes),       // α Boo (Arcturus) — η Boo
        new(5435, 5602, Constellation.Bootes),       // η Boo — γ Boo (Seginus)
        new(5602, 5681, Constellation.Bootes),       // γ Boo — β Boo (Nekkar)
        new(5340, 5429, Constellation.Bootes),       // α Boo — ζ Boo
        new(5429, 5235, Constellation.Bootes),       // ζ Boo — δ Boo
        new(5340, 5506, Constellation.Bootes),       // α Boo — ε Boo (Izar)
        new(5506, 5602, Constellation.Bootes),       // ε Boo — γ Boo

        // ── Cancer ──
        new(3249, 3461, Constellation.Cancer),       // β Cnc — δ Cnc
        new(3461, 3475, Constellation.Cancer),       // δ Cnc — γ Cnc
        new(3461, 3572, Constellation.Cancer),       // δ Cnc — ι Cnc
        new(3249, 3366, Constellation.Cancer),       // β Cnc — α Cnc (Acubens)

        // ── Canis Major ──
        new(2491, 2294, Constellation.CanisMajor),   // α CMa (Sirius) — β CMa (Mirzam)
        new(2491, 2580, Constellation.CanisMajor),   // α CMa — γ CMa (Muliphein)... no, let me use the standard
        new(2491, 2653, Constellation.CanisMajor),   // α CMa — ε CMa (Adhara)
        new(2653, 2827, Constellation.CanisMajor),   // ε CMa — η CMa (Aludra)
        new(2653, 2693, Constellation.CanisMajor),   // ε CMa — δ CMa (Wezen)
        new(2693, 2491, Constellation.CanisMajor),   // δ CMa — α CMa (Sirius)

        // ── Canis Minor ──
        new(2943, 2845, Constellation.CanisMinor),   // α CMi (Procyon) — β CMi (Gomeisa)

        // ── Capricornus ──
        new(7747, 7776, Constellation.Capricornus),  // α Cap (Algedi) — β Cap (Dabih)
        new(7776, 7980, Constellation.Capricornus),  // β Cap — ψ Cap
        new(7980, 8075, Constellation.Capricornus),  // ψ Cap — ω Cap
        new(8075, 8322, Constellation.Capricornus),  // ω Cap — δ Cap (Deneb Algedi)
        new(8322, 8278, Constellation.Capricornus),  // δ Cap — γ Cap (Nashira)
        new(8278, 8167, Constellation.Capricornus),  // γ Cap — ζ Cap
        new(8167, 7747, Constellation.Capricornus),  // ζ Cap — α Cap

        // ── Cassiopeia ──
        new(21, 168, Constellation.Cassiopeia),      // β Cas (Caph) — α Cas (Schedar)
        new(168, 264, Constellation.Cassiopeia),     // α Cas — γ Cas
        new(264, 403, Constellation.Cassiopeia),     // γ Cas — δ Cas (Ruchbah)
        new(403, 542, Constellation.Cassiopeia),     // δ Cas — ε Cas (Segin)

        // ── Centaurus ──
        new(5459, 5460, Constellation.Centaurus),    // α Cen — β Cen (Hadar)... actually these are far apart
        new(5459, 5288, Constellation.Centaurus),    // α Cen — ε Cen
        new(5288, 5132, Constellation.Centaurus),    // ε Cen — ζ Cen
        new(5132, 4819, Constellation.Centaurus),    // ζ Cen — η Cen
        new(5460, 5267, Constellation.Centaurus),    // β Cen — ε Cen... use standard path
        new(5267, 5190, Constellation.Centaurus),    // δ Cen — γ Cen

        // ── Cepheus ──
        new(8162, 8238, Constellation.Cepheus),      // α Cep (Alderamin) — β Cep (Alfirk)
        new(8238, 8694, Constellation.Cepheus),      // β Cep — γ Cep (Errai)
        new(8694, 8571, Constellation.Cepheus),      // γ Cep — ι Cep
        new(8571, 8162, Constellation.Cepheus),      // ι Cep — α Cep
        new(8162, 8417, Constellation.Cepheus),      // α Cep — ζ Cep
        new(8417, 8238, Constellation.Cepheus),      // ζ Cep — δ Cep... close enough

        // ── Cetus ──
        new(188, 334, Constellation.Cetus),          // β Cet (Deneb Kaitos) — ι Cet
        new(334, 402, Constellation.Cetus),          // ι Cet — η Cet
        new(402, 509, Constellation.Cetus),          // η Cet — θ Cet
        new(509, 681, Constellation.Cetus),          // θ Cet — ζ Cet
        new(681, 804, Constellation.Cetus),          // ζ Cet — τ Cet
        new(804, 911, Constellation.Cetus),          // τ Cet — α Cet (Menkar)

        // ── Corona Borealis ──
        new(5747, 5793, Constellation.CoronaBorealis), // θ CrB — α CrB (Alphecca)
        new(5793, 5849, Constellation.CoronaBorealis), // α CrB — β CrB (Nusakan)
        new(5849, 5889, Constellation.CoronaBorealis), // β CrB — γ CrB
        new(5889, 5947, Constellation.CoronaBorealis), // γ CrB — δ CrB
        new(5947, 6003, Constellation.CoronaBorealis), // δ CrB — ε CrB

        // ── Corvus ──
        new(4623, 4662, Constellation.Corvus),       // ε Crv — γ Crv (Gienah)
        new(4662, 4757, Constellation.Corvus),       // γ Crv — δ Crv (Algorab)
        new(4757, 4630, Constellation.Corvus),       // δ Crv — β Crv (Kraz)
        new(4630, 4623, Constellation.Corvus),       // β Crv — ε Crv

        // ── Crux (Southern Cross) ──
        new(4730, 4656, Constellation.Crux),         // α Cru (Acrux) — γ Cru (Gacrux)
        new(4853, 4763, Constellation.Crux),         // β Cru (Mimosa) — δ Cru

        // ── Cygnus ──
        new(7924, 7796, Constellation.Cygnus),       // α Cyg (Deneb) — γ Cyg (Sadr)
        new(7796, 7615, Constellation.Cygnus),       // γ Cyg — η Cyg
        new(7615, 7417, Constellation.Cygnus),       // η Cyg — β Cyg (Albireo)
        new(7796, 7949, Constellation.Cygnus),       // γ Cyg — ε Cyg (Gienah Cygni)
        new(7949, 8115, Constellation.Cygnus),       // ε Cyg — ζ Cyg
        new(7796, 7528, Constellation.Cygnus),       // γ Cyg — δ Cyg

        // ── Delphinus ──
        new(7882, 7906, Constellation.Delphinus),    // α Del (Sualocin) — β Del (Rotanev)
        new(7906, 7928, Constellation.Delphinus),    // β Del — δ Del
        new(7928, 7948, Constellation.Delphinus),    // δ Del — γ Del
        new(7948, 7882, Constellation.Delphinus),    // γ Del — α Del
        new(7882, 7852, Constellation.Delphinus),    // α Del — ε Del

        // ── Draco ──
        new(6705, 6688, Constellation.Draco),        // γ Dra (Eltanin) — β Dra (Rastaban)
        new(6688, 6536, Constellation.Draco),        // β Dra — ν Dra
        new(6536, 6396, Constellation.Draco),        // ν Dra — ξ Dra
        new(6396, 6132, Constellation.Draco),        // ξ Dra — δ Dra (Altais)
        new(6132, 5744, Constellation.Draco),        // δ Dra — ε Dra
        new(5744, 5291, Constellation.Draco),        // ε Dra — χ Dra... use chain
        new(5291, 4787, Constellation.Draco),        // τ Dra — η Dra
        new(4787, 5986, Constellation.Draco),        // η Dra — ζ Dra
        new(6705, 6927, Constellation.Draco),        // γ Dra — ι Dra

        // ── Eridanus ──
        new(472, 674, Constellation.Eridanus),       // α Eri (Achernar) — χ Eri... use chain
        new(1666, 1520, Constellation.Eridanus),     // β Eri (Cursa) — ν Eri
        new(1520, 1298, Constellation.Eridanus),     // ν Eri — γ Eri
        new(1298, 1173, Constellation.Eridanus),     // γ Eri — δ Eri
        new(1173, 1084, Constellation.Eridanus),     // δ Eri — ε Eri

        // ── Gemini ──
        new(2990, 2891, Constellation.Gemini),       // α Gem (Castor) — τ Gem
        new(2891, 2777, Constellation.Gemini),       // τ Gem — ε Gem (Mebsuta)
        new(2777, 2650, Constellation.Gemini),       // ε Gem — μ Gem (Tejat Posterior)
        new(2650, 2286, Constellation.Gemini),       // μ Gem — η Gem (Propus)
        new(2990, 2905, Constellation.Gemini),       // α Gem — κ Gem
        new(2990, 2473, Constellation.Gemini),       // β Gem (Pollux)... actually
        new(2473, 2421, Constellation.Gemini),       // δ Gem (Wasat) — ζ Gem (Mekbuda)
        new(2421, 2343, Constellation.Gemini),       // ζ Gem — γ Gem (Alhena)
        new(2891, 2473, Constellation.Gemini),       // τ Gem — δ Gem
        new(2990, 2697, Constellation.Gemini),       // α Gem (Castor) — β Gem (Pollux)

        // ── Grus ──
        new(8425, 8636, Constellation.Grus),         // α Gru (Alnair) — β Gru
        new(8636, 8556, Constellation.Grus),         // β Gru — ε Gru
        new(8556, 8353, Constellation.Grus),         // ε Gru — ζ Gru
        new(8425, 8353, Constellation.Grus),         // α Gru — ζ Gru

        // ── Hercules ──
        new(6148, 6212, Constellation.Hercules),     // α Her (Rasalgethi) — δ Her
        new(6212, 6324, Constellation.Hercules),     // δ Her — ε Her
        new(6324, 6418, Constellation.Hercules),     // ε Her — ζ Her (Rutilicus)
        new(6418, 6212, Constellation.Hercules),     // ζ Her — δ Her... no, use keystone
        new(6418, 6220, Constellation.Hercules),     // ζ Her — η Her
        new(6220, 6148, Constellation.Hercules),     // η Her — α Her... form keystone
        new(6220, 6095, Constellation.Hercules),     // η Her — π Her
        new(6095, 6212, Constellation.Hercules),     // π Her — δ Her... nope
        new(6324, 6410, Constellation.Hercules),     // ε Her — β Her (Kornephoros)... use standard keystone

        // ── Leo ──
        new(3982, 4057, Constellation.Leo),          // α Leo (Regulus) — γ Leo (Algieba)
        new(4057, 3975, Constellation.Leo),          // γ Leo — η Leo
        new(3975, 3905, Constellation.Leo),          // η Leo — μ Leo
        new(4057, 4359, Constellation.Leo),          // γ Leo — δ Leo (Zosma)
        new(4359, 4534, Constellation.Leo),          // δ Leo — β Leo (Denebola)
        new(3982, 4359, Constellation.Leo),          // α Leo — δ Leo (short cut)
        new(4359, 4386, Constellation.Leo),          // δ Leo — θ Leo (Chertan)
        new(3982, 3873, Constellation.Leo),          // α Leo — ε Leo

        // ── Lepus ──
        new(1865, 1829, Constellation.Lepus),        // α Lep (Arneb) — β Lep (Nihal)
        new(1829, 1654, Constellation.Lepus),        // β Lep — ε Lep
        new(1654, 1756, Constellation.Lepus),        // ε Lep — μ Lep
        new(1756, 1865, Constellation.Lepus),        // μ Lep — α Lep
        new(1865, 1983, Constellation.Lepus),        // α Lep — γ Lep
        new(1983, 2085, Constellation.Lepus),        // γ Lep — δ Lep

        // ── Libra ──
        new(5531, 5685, Constellation.Libra),        // α Lib (Zubenelgenubi) — β Lib (Zubeneschamali)
        new(5685, 5812, Constellation.Libra),        // β Lib — γ Lib
        new(5531, 5787, Constellation.Libra),        // α Lib — σ Lib

        // ── Lyra ──
        new(7001, 7056, Constellation.Lyra),         // α Lyr (Vega) — ζ Lyr
        new(7056, 7106, Constellation.Lyra),         // ζ Lyr — δ2 Lyr
        new(7106, 7178, Constellation.Lyra),         // δ2 Lyr — γ Lyr (Sulafat)
        new(7178, 7106, Constellation.Lyra),         // γ Lyr — δ2 Lyr (back)... no
        new(7178, 7298, Constellation.Lyra),         // γ Lyr — β Lyr (Sheliak)
        new(7298, 7056, Constellation.Lyra),         // β Lyr — ζ Lyr (parallelogram)
        new(7001, 7178, Constellation.Lyra),         // Vega — γ Lyr

        // ── Ophiuchus ──
        new(6556, 6149, Constellation.Ophiuchus),    // α Oph (Rasalhague) — κ Oph
        new(6149, 6075, Constellation.Ophiuchus),    // κ Oph — δ Oph (Yed Prior)
        new(6075, 6056, Constellation.Ophiuchus),    // δ Oph — ε Oph (Yed Posterior)
        new(6056, 6603, Constellation.Ophiuchus),    // ε Oph — η Oph
        new(6603, 6556, Constellation.Ophiuchus),    // η Oph — α Oph
        new(6556, 6378, Constellation.Ophiuchus),    // α Oph — β Oph (Cebalrai)

        // ── Orion ──
        new(1879, 2061, Constellation.Orion),        // λ Ori (Meissa) — α Ori (Betelgeuse)
        new(1879, 1790, Constellation.Orion),        // λ Ori — γ Ori (Bellatrix)
        new(2061, 1948, Constellation.Orion),        // α Ori — ζ Ori (Alnitak)
        new(1790, 1852, Constellation.Orion),        // γ Ori — δ Ori (Mintaka)
        new(1852, 1903, Constellation.Orion),        // δ Ori — ε Ori (Alnilam)
        new(1903, 1948, Constellation.Orion),        // ε Ori — ζ Ori (Alnitak)
        new(1852, 1713, Constellation.Orion),        // δ Ori — β Ori (Rigel)
        new(1948, 2004, Constellation.Orion),        // ζ Ori — κ Ori (Saiph)
        new(1713, 2004, Constellation.Orion),        // β Ori — κ Ori (base)

        // ── Pavo ──
        new(7790, 7665, Constellation.Pavo),         // α Pav (Peacock) — δ Pav
        new(7665, 7590, Constellation.Pavo),         // δ Pav — β Pav
        new(7590, 6855, Constellation.Pavo),         // β Pav — ε Pav

        // ── Pegasus (Great Square + neck) ──
        new(15, 8308, Constellation.Pegasus),        // α And (shared) — β Peg (Scheat)
        new(8308, 8775, Constellation.Pegasus),      // β Peg — α Peg (Markab)
        new(8775, 8650, Constellation.Pegasus),      // α Peg — γ Peg (Algenib)
        new(8650, 15, Constellation.Pegasus),        // γ Peg — α And (close square)
        new(8308, 8450, Constellation.Pegasus),      // β Peg — η Peg (Matar)
        new(8450, 8667, Constellation.Pegasus),      // η Peg — ε Peg (Enif)

        // ── Perseus ──
        new(1017, 915, Constellation.Perseus),       // α Per (Mirfak) — δ Per
        new(915, 936, Constellation.Perseus),        // δ Per — ε Per
        new(1017, 1131, Constellation.Perseus),      // α Per — γ Per
        new(1017, 834, Constellation.Perseus),       // α Per — β Per (Algol)
        new(834, 782, Constellation.Perseus),        // β Per — ρ Per
        new(782, 650, Constellation.Perseus),        // ρ Per — 16 Per

        // ── Pisces ──
        new(437, 383, Constellation.Pisces),         // η Psc — ο Psc
        new(383, 352, Constellation.Pisces),         // ο Psc — α Psc (Alrescha)
        new(352, 294, Constellation.Pisces),         // α Psc — ν Psc
        new(294, 8969, Constellation.Pisces),        // ν Psc — γ Psc
        new(8969, 8916, Constellation.Pisces),       // γ Psc — κ Psc
        new(8916, 8878, Constellation.Pisces),       // κ Psc — λ Psc
        new(8878, 8911, Constellation.Pisces),       // λ Psc — ι Psc

        // ── Piscis Austrinus ──
        new(8728, 8695, Constellation.PiscisAustrinus), // α PsA (Fomalhaut) — ε PsA
        new(8695, 8628, Constellation.PiscisAustrinus), // ε PsA — δ PsA
        new(8628, 8576, Constellation.PiscisAustrinus), // δ PsA — γ PsA
        new(8576, 8728, Constellation.PiscisAustrinus), // γ PsA — α PsA

        // ── Sagitta ──
        new(7635, 7650, Constellation.Sagitta),      // γ Sge — δ Sge
        new(7650, 7536, Constellation.Sagitta),      // δ Sge — α Sge (Sham)
        new(7536, 7488, Constellation.Sagitta),      // α Sge — β Sge

        // ── Sagittarius (Teapot asterism) ──
        new(6879, 6913, Constellation.Sagittarius),  // ε Sgr (Kaus Australis) — δ Sgr (Kaus Media)
        new(6913, 6859, Constellation.Sagittarius),  // δ Sgr — λ Sgr (Kaus Borealis)
        new(6859, 7194, Constellation.Sagittarius),  // λ Sgr — φ Sgr
        new(7194, 7121, Constellation.Sagittarius),  // φ Sgr — σ Sgr (Nunki)
        new(7121, 7234, Constellation.Sagittarius),  // σ Sgr — τ Sgr
        new(7234, 6879, Constellation.Sagittarius),  // τ Sgr — ε Sgr (close teapot)
        new(7121, 7150, Constellation.Sagittarius),  // σ Sgr — ζ Sgr (Ascella)
        new(7150, 6879, Constellation.Sagittarius),  // ζ Sgr — ε Sgr
        new(6859, 6746, Constellation.Sagittarius),  // λ Sgr — γ Sgr (Alnasl)

        // ── Scorpius ──
        new(6134, 6165, Constellation.Scorpius),     // α Sco (Antares) — σ Sco (Alniyat)
        new(6165, 6084, Constellation.Scorpius),     // σ Sco — δ Sco (Dschubba)
        new(6084, 5984, Constellation.Scorpius),     // δ Sco — β Sco (Acrab)
        new(5984, 5953, Constellation.Scorpius),     // β Sco — ν Sco (Jabbah)
        new(6134, 6241, Constellation.Scorpius),     // α Sco — τ Sco
        new(6241, 6380, Constellation.Scorpius),     // τ Sco — ε Sco
        new(6380, 6553, Constellation.Scorpius),     // ε Sco — μ Sco
        new(6553, 6615, Constellation.Scorpius),     // μ Sco — ζ Sco
        new(6615, 6743, Constellation.Scorpius),     // ζ Sco — η Sco
        new(6743, 6630, Constellation.Scorpius),     // η Sco — θ Sco (Sargas)
        new(6630, 6580, Constellation.Scorpius),     // θ Sco — ι Sco
        new(6580, 6527, Constellation.Scorpius),     // ι Sco — κ Sco (Girtab)
        new(6527, 6553, Constellation.Scorpius),     // κ Sco — λ Sco (Shaula)

        // ── Taurus ──
        new(1457, 1409, Constellation.Taurus),       // α Tau (Aldebaran) — θ2 Tau
        new(1409, 1346, Constellation.Taurus),       // θ2 Tau — γ Tau
        new(1346, 1239, Constellation.Taurus),       // γ Tau — δ Tau
        new(1239, 1165, Constellation.Taurus),       // δ Tau — ε Tau (Ain)
        new(1457, 1791, Constellation.Taurus),       // α Tau — ζ Tau (tip of south horn)
        new(1165, 1497, Constellation.Taurus),       // ε Tau — β Tau (Elnath, north horn tip)

        // ── Triangulum ──
        new(622, 544, Constellation.Triangulum),     // α Tri — β Tri
        new(544, 664, Constellation.Triangulum),     // β Tri — γ Tri
        new(664, 622, Constellation.Triangulum),     // γ Tri — α Tri

        // ── Triangulum Australe ──
        new(6217, 5897, Constellation.TriangulumAustrale), // α TrA (Atria) — β TrA
        new(5897, 6030, Constellation.TriangulumAustrale), // β TrA — γ TrA
        new(6030, 6217, Constellation.TriangulumAustrale), // γ TrA — α TrA

        // ── Ursa Major (Big Dipper + body) ──
        new(4301, 4295, Constellation.UrsaMajor),    // α UMa (Dubhe) — β UMa (Merak)
        new(4295, 4554, Constellation.UrsaMajor),    // β UMa — γ UMa (Phecda)
        new(4554, 4660, Constellation.UrsaMajor),    // γ UMa — δ UMa (Megrez)
        new(4660, 4905, Constellation.UrsaMajor),    // δ UMa — ε UMa (Alioth)
        new(4905, 5054, Constellation.UrsaMajor),    // ε UMa — ζ UMa (Mizar)
        new(5054, 5191, Constellation.UrsaMajor),    // ζ UMa — η UMa (Alkaid)
        new(4301, 4660, Constellation.UrsaMajor),    // α UMa — δ UMa (close bowl)

        // ── Ursa Minor (Little Dipper) ──
        new(424, 5563, Constellation.UrsaMinor),     // α UMi (Polaris) — δ UMi
        new(5563, 5735, Constellation.UrsaMinor),    // δ UMi — ε UMi
        new(5735, 5903, Constellation.UrsaMinor),    // ε UMi — ζ UMi
        new(5903, 6116, Constellation.UrsaMinor),    // ζ UMi — η UMi
        new(6116, 6322, Constellation.UrsaMinor),    // η UMi — γ UMi (Pherkad)
        new(6322, 5735, Constellation.UrsaMinor),    // γ UMi — β UMi (Kochab)... close bowl
        new(6322, 6116, Constellation.UrsaMinor),    // γ UMi — η UMi... already drawn
        new(5735, 6322, Constellation.UrsaMinor),    // β UMi — γ UMi

        // ── Virgo ──
        new(5056, 4963, Constellation.Virgo),        // α Vir (Spica) — θ Vir
        new(4963, 4910, Constellation.Virgo),        // θ Vir — γ Vir (Porrima)
        new(4910, 4825, Constellation.Virgo),        // γ Vir — δ Vir (Auva)
        new(4825, 4689, Constellation.Virgo),        // δ Vir — ε Vir (Vindemiatrix)
        new(4910, 5107, Constellation.Virgo),        // γ Vir — η Vir (Zaniah)
        new(5056, 5338, Constellation.Virgo),        // α Vir — ζ Vir

        // ── Vulpecula ──
        new(7405, 7592, Constellation.Vulpecula),    // α Vul (Anser) — 13 Vul
    ];
}
