
var folder = Path.Combine("C:", "Temp", "Astro", "SharpCap Captures", "Focusing", "ts2022-04-13T123214-fp17384");

foreach (var file in new DirectoryInfo(folder).GetFiles("*.fits"))
{
    var stars = Astap.analyse_fits(Path.Combine(folder, file.FullName), 10, 200, out var medianHFD, out var medianFWHM, out var background);

    Console.WriteLine("File {0} contains {1} stars, median HFD is {2} median FWHM is {3}", file, stars, medianHFD, medianFWHM);
}
