using Astap.Lib;
using System.Diagnostics;

var camIdx = 0;
if (AstapLib.find_asi_camera_by_index(camIdx, out var camId, out var error))
{
    Console.WriteLine($"Found camera #{camId} at index {camIdx}");
    if (AstapLib.connect_asi_camera(camId, out error))
    {
        Console.WriteLine($"Camera #{camId} connected!");

        AstapLib.disconnect_asi_camera(camId, out _);
    }
    else
    {
        Console.WriteLine($"Failed to connect to camera #{camId} at index {camIdx} due to: {error}");
    }
}
else
{
    Console.WriteLine($"Could not find a camera at index {camIdx} due to: {error}");
}

var folder = Path.Combine("C:", "Temp", "Astro", "SharpCap Captures", "Focusing", "ts2022-04-13T123214-fp17384");

Console.ReadLine();

foreach (var file in new DirectoryInfo(folder).GetFiles("*.fits"))
{
    var sw = Stopwatch.StartNew();
    var stars = AstapLib.analyse_fits(Path.Combine(folder, file.FullName), 10, 200, out var medianHFD, out var medianFWHM, out var background);
    sw.Stop();

    Console.WriteLine($"File {file} contains {stars} stars, median HFD is {medianHFD} median FWHM is {medianFWHM} background is {background}, total time: {sw.ElapsedMilliseconds}");
}
