namespace Astap.Lib.Imaging;

public readonly record struct ImagedStar(double HFD, double StarFWHM, double SNR, double Flux, double XCentroid, double YCentroid);
