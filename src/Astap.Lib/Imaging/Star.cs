namespace Astap.Lib.Imaging;

public readonly record struct Star(double HFD, double StarFWHM, double SNR, double Flux, double XCentroid, double YCentroid);
