using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Constellation stick figures (IAU modern Western) as polylines of HIP (Hipparcos) star numbers.
/// Data from Stellarium's modern sky culture. Each constellation maps to one or more polylines;
/// consecutive HIP numbers in a polyline are connected by lines.
/// Serpens has entries for Serpens (both polylines), SerpensCaput (head), and SerpensCauda (tail).
/// </summary>
public static class ConstellationFigures
{
    /// <summary>
    /// Try to get the stick figure for a constellation. Returns false if no figure is defined.
    /// </summary>
    public static bool TryGetFigure(Constellation constellation, out ImmutableArray<ImmutableArray<int>> figure)
        => _figures.TryGetValue(constellation, out figure);

    /// <summary>
    /// Stick figure polylines per constellation. Each value is a jagged array of HIP numbers:
    /// the outer array holds separate polylines, the inner arrays hold the star sequence.
    /// </summary>
    private static readonly FrozenDictionary<Constellation, ImmutableArray<ImmutableArray<int>>> _figures =
        new Dictionary<Constellation, ImmutableArray<ImmutableArray<int>>>
        {
            [Constellation.Andromeda] = [[677, 3092, 5447], [9640, 5447, 4436, 3881]],
            [Constellation.Antlia] = [[51172, 48926]],
            [Constellation.Apus] = [[72370, 81065, 81852]],
            [Constellation.Aquarius] = [[106278, 109074, 110395, 110960, 111497, 112961, 114855, 115438], [109074, 110003, 109139], [110003, 111123, 112716, 113136, 114341], [102618, 106278]],
            [Constellation.Aquila] = [[98036, 97649, 97278], [97649, 95501, 97804], [99473, 97804], [95501, 93747, 93244], [95501, 93805]],
            [Constellation.Ara] = [[88714, 85792, 83081, 82363, 85727, 85267, 85258, 88714]],
            [Constellation.Aries] = [[13209, 9884, 8903, 8832]],
            [Constellation.Auriga] = [[28380, 28360, 24608, 23453, 23015], [25428, 23015], [25428, 28380]],
            [Constellation.Bootes] = [[71795, 69673, 72105, 74666, 73555, 71075, 71053, 69673, 67927, 67459]],
            [Constellation.Caelum] = [[21060, 21770, 21861]],
            [Constellation.Camelopardalis] = [[16228, 18505, 22783], [16228, 17959, 22783], [17959, 25110]],
            [Constellation.Cancer] = [[43103, 42806, 40843], [42806, 42911, 40526], [42911, 44066]],
            [Constellation.CanesVenatici] = [[61317, 63125]],
            [Constellation.CanisMajor] = [[33160, 34045, 33347, 32349, 33977, 34444, 35037, 35904], [33579, 33856, 34444], [33856, 33165, 31592, 31416], [31592, 30324], [31592, 32349], [33579, 32759], [30122, 33579], [33347, 33160]],
            [Constellation.CanisMinor] = [[37279, 36188]],
            [Constellation.Capricornus] = [[100064, 100345, 104139, 105515, 106985, 107556], [105515, 105881, 104139], [100345, 102485], [104139, 102978]],
            [Constellation.Carina] = [[45238, 50099, 52419, 52468, 54463, 53253, 51232, 50371, 45556], [42568, 41037, 30438], [45080, 45556], [45080, 42568], [30438, 31685], [41037, 39429]],
            [Constellation.Cassiopeia] = [[8886, 6686, 4427, 3179, 746]],
            [Constellation.Centaurus] = [[71683, 68702, 66657, 68002, 68282, 67472, 67464, 65936, 65109], [67464, 68933], [67472, 71352, 73334], [68002, 61932, 60823, 59196, 56480, 56561]],
            [Constellation.Cepheus] = [[109492, 112724, 106032, 105199, 109492], [112724, 116727, 106032]],
            [Constellation.Cetus] = [[10324, 11484], [8102, 3419, 1562], [3419, 5364, 6537, 8645, 11345, 12390, 12770, 11783, 8102], [10826, 12390], [10826, 12387, 12706, 14135, 13954, 12828, 11484, 12093, 12706]],
            [Constellation.Chamaeleon] = [[40702, 51839, 60000]],
            [Constellation.Circinus] = [[71908, 75323], [71908, 74824]],
            [Constellation.Columba] = [[30277, 29807, 28199, 27628, 28328], [27628, 26634, 25859]],
            [Constellation.ComaBerenices] = [[64241, 64394, 60742]],
            [Constellation.CoronaAustralis] = [[91875, 92989, 93174, 93825, 94114, 94160, 94005, 93542, 92953], [91875, 90887]],
            [Constellation.CoronaBorealis] = [[76127, 75695, 76267, 76952, 77512, 78159, 78493]],
            [Constellation.Corvus] = [[61174, 60965, 59803, 59316, 59199], [59316, 61359, 60965]],
            [Constellation.Crater] = [[53740, 54682, 55705, 55282, 53740], [55282, 55687, 56633, 58188, 57283, 55705]],
            [Constellation.Crux] = [[61084, 60718], [62434, 59747]],
            [Constellation.Cygnus] = [[94779, 95853, 97165, 100453, 102098], [100453, 102488, 104732, 107310], [100453, 98110, 95947]],
            [Constellation.Delphinus] = [[101421, 101769, 101958, 102532, 102281, 101769]],
            [Constellation.Dorado] = [[27100, 27890, 26069, 27100], [26069, 21281, 19893]],
            [Constellation.Draco] = [[87585, 87833, 85670, 85829, 87585, 94376, 97433, 94648, 89937, 83895, 80331, 78527, 75458, 68756, 61281, 56211]],
            [Constellation.Equuleus] = [[104521, 104858, 105570, 104987, 104521]],
            [Constellation.Eridanus] = [[7588, 9007, 10602, 11407, 12413, 12486, 13847, 15510, 17797, 17874, 20042, 20535, 21393, 17651, 16611, 15474, 14146, 12843, 13701, 15197, 16537, 17378, 21444, 22109, 22701, 23875, 23972, 21594]],
            [Constellation.Fornax] = [[13147, 14879]],
            [Constellation.Gemini] = [[31681, 34088, 35550, 35350, 32362], [35550, 36962, 37740], [36962, 37826], [36962, 36046, 34693, 36850], [34693, 33018], [34693, 32246, 30883], [32246, 30343, 29655, 28734]],
            [Constellation.Grus] = [[114131, 110997, 109268, 112122, 114421, 114131], [112122, 113638], [112122, 112623], [109268, 109111, 108085]],
            [Constellation.Hercules] = [[86414, 87808, 85112, 84606, 84380, 81833, 81126, 79992, 77760], [81833, 81693, 80816, 80170], [81693, 83207, 85693, 84379], [86974, 87933, 88794], [83207, 84380], [86974, 85693]],
            [Constellation.Horologium] = [[19747, 12484, 14240]],
            [Constellation.Hydra] = [[42799, 42402, 42313, 43109, 43234, 42799], [43234, 43813, 45336, 46776, 46509, 46390, 45751, 47452, 48356, 49841, 51069, 52943, 54204, 56343, 57936, 64166, 64962, 68895, 69415, 70306, 72571]],
            [Constellation.Hydrus] = [[2021, 17678, 12394, 11001, 9236]],
            [Constellation.Indus] = [[105319, 101772, 103227, 105319]],
            [Constellation.Lacerta] = [[109937, 111104, 111022, 110609, 110538, 111169, 111022]],
            [Constellation.Leo] = [[57632, 54879, 49669, 49583, 50583, 54872, 57632], [50583, 50335, 48455, 47908], [54872, 54879]],
            [Constellation.LeoMinor] = [[53229, 51233, 49593, 46952], [49593, 53229]],
            [Constellation.Lepus] = [[28910, 28103, 27288, 25985, 24305], [25985, 27654, 27072, 25606, 23685], [25985, 25606], [24305, 24845], [24305, 24327], [23685, 24305], [24327, 24244], [24845, 24873]],
            [Constellation.Libra] = [[77853, 76333, 74785, 72622, 73714, 76333]],
            [Constellation.Lupus] = [[77634, 78970, 78384, 77634], [78384, 76297, 75141, 75177], [75141, 73273], [76297, 76552, 74395, 71860], [74395, 71536], [71860, 70576], [71860, 73273]],
            [Constellation.Lynx] = [[45860, 45688, 44700, 44248, 41075, 36145, 33449, 30060]],
            [Constellation.Lyra] = [[91262, 91971, 92420, 93194, 92791, 91971]],
            [Constellation.Mensa] = [[25918, 21949]],
            [Constellation.Microscopium] = [[105140, 103738, 102831]],
            [Constellation.Monoceros] = [[29651, 34769], [30867, 34769, 32533, 30419, 31216, 31978], [32533, 31978, 30665], [34769, 39211, 39863], [39211, 37447]],
            [Constellation.Musca] = [[62322, 57363, 61199, 61585, 62322]],
            [Constellation.Norma] = [[79509, 80000, 80582, 78639, 80000], [78639, 79509]],
            [Constellation.Octans] = [[107089, 112405, 70638, 107089]],
            [Constellation.Ophiuchus] = [[86032, 86742], [84012, 86742], [86032, 83000, 79882, 81377, 84012, 85755]],
            [Constellation.Orion] = [[26727, 26311, 25930], [29434, 29426], [29434, 28716, 27913], [29426, 29038, 27913], [29426, 28614, 27989, 26727, 27366, 24436, 25930, 25336, 26207, 27989], [25336, 22449, 22549, 22730, 22797, 23123], [22449, 22509, 22845], [29038, 28614]],
            [Constellation.Pavo] = [[100751, 105858, 102395, 99240, 100751], [99240, 98495, 91792, 93015, 99240], [93015, 92609, 90098, 88866, 92609], [88866, 86929]],
            [Constellation.Pegasus] = [[1067, 113963], [113881, 112158, 109352], [113881, 112748, 112440, 109176, 107354], [113963, 112447, 112029, 109427, 107315], [677, 113881], [677, 1067], [113881, 113963]],
            [Constellation.Perseus] = [[17448, 18246, 18614, 18532, 17358, 15863, 14328, 13268], [15863, 14576, 14354, 13254]],
            [Constellation.Phoenix] = [[5348, 5165, 2072, 5348], [5165, 7083, 8837, 5165, 6867, 2072, 2081, 765, 2072]],
            [Constellation.Pictor] = [[32607, 27530, 27321]],
            [Constellation.Pisces] = [[4889, 5742], [4889, 6193, 5742, 7097, 8198, 9487, 8833, 7884, 7007, 4906, 3760, 1645, 118268, 116771, 117245, 116928, 115738, 114971, 115227, 115830, 116771]],
            [Constellation.PiscisAustrinus] = [[113368, 111954, 108661, 107608, 109422, 111188, 113246]],
            [Constellation.Puppis] = [[39757, 38146, 35264, 31685, 32768, 36377, 39429, 39757]],
            [Constellation.Pyxis] = [[42515, 42828, 43409]],
            [Constellation.Reticulum] = [[19780, 19921, 18597, 17440, 19780]],
            [Constellation.Sagitta] = [[96837, 97365, 96757], [97365, 98337, 98920]],
            [Constellation.Sagittarius] = [[89931, 90496], [89642, 90185, 88635, 87072], [88635, 89931, 90185, 93506, 92041, 89931], [92041, 90496, 89341], [93506, 93864, 92855, 92041], [92855, 93085, 93683, 94820, 95168], [93864, 96406, 98688, 98412, 98032, 95347], [98032, 95294]],
            [Constellation.Scorpius] = [[85927, 86670, 87073, 86228, 84143, 82671, 82514, 82396, 81266, 80763, 78401], [80763, 78265], [80763, 78820]],
            [Constellation.Sculptor] = [[116231, 4577, 115102, 116231]],
            [Constellation.Scutum] = [[92175, 92202, 92814, 90595, 91117, 92175]],
            // Serpens is composed from SerpensCaput + SerpensCauda via the extension
            [Constellation.SerpensCaput] = [[79593, 77516, 77622, 77070, 76276, 77233, 78072, 77450, 77233]],
            [Constellation.SerpensCauda] = [[92946, 90441, 89962, 88670, 88048, 86565, 86263, 84880]],
            [Constellation.Sextans] = [[51437, 49641]],
            [Constellation.Taurus] = [[25428, 21881, 20889], [21421, 26451], [20205, 20455], [20205, 18724, 15900], [21421, 20889], [21421, 20894, 20205], [20889, 20648, 20455, 17847]],
            [Constellation.Telescopium] = [[90568, 90422]],
            [Constellation.Triangulum] = [[10670, 10064, 8796, 10670]],
            [Constellation.TriangulumAustrale] = [[82273, 74946, 77952, 82273]],
            [Constellation.Tucana] = [[110130, 114996, 1599], [114996, 2484]],
            [Constellation.UrsaMajor] = [[67301, 65378, 62956, 59774, 54061, 53910, 58001, 59774], [58001, 57399, 54539, 50372], [54539, 50801], [53910, 48402, 46853, 44471], [46853, 44127], [48402, 48319, 41704, 46733, 54061]],
            [Constellation.UrsaMinor] = [[11767, 85822, 82080, 77055, 79822, 75097, 72607, 77055]],
            [Constellation.Vela] = [[39953, 42536, 42913, 45941, 48774, 52727, 51986, 50191, 46651, 44816, 39953]],
            [Constellation.Virgo] = [[57380, 60129, 61941, 65474, 69427, 69701, 71957], [65474, 66249, 68520, 72220], [66249, 63090, 63608], [63090, 61941]],
            [Constellation.Volans] = [[37504, 34481, 39794, 37504], [39794, 35228], [39794, 41312, 44382, 39794]],
            [Constellation.Vulpecula] = [[95771, 98543]],
        }.ToFrozenDictionary();
}

public static class ConstellationFigureExtensions
{
    extension(Constellation constellation)
    {
        /// <summary>
        /// Returns the stick figure polylines for this constellation as HIP star number sequences.
        /// Serpens composes its figure from SerpensCaput + SerpensCauda.
        /// Returns an empty array if no figure is defined.
        /// </summary>
        public ImmutableArray<ImmutableArray<int>> Figure
        {
            get
            {
                if (ConstellationFigures.TryGetFigure(constellation, out var figure))
                {
                    return figure;
                }

                // Serpens: compose from Caput + Cauda
                if (constellation == Constellation.Serpens
                    && ConstellationFigures.TryGetFigure(Constellation.SerpensCaput, out var caput)
                    && ConstellationFigures.TryGetFigure(Constellation.SerpensCauda, out var cauda))
                {
                    return [..caput, ..cauda];
                }

                return [];
            }
        }

        /// <summary>
        /// Returns true if this constellation has a defined stick figure.
        /// </summary>
        public bool HasFigure
            => ConstellationFigures.TryGetFigure(constellation, out _)
            || (constellation == Constellation.Serpens
                && ConstellationFigures.TryGetFigure(Constellation.SerpensCaput, out _));
    }
}
