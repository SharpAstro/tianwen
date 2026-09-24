using System;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// An <see cref="Imaging.Image"/> over planes rented from <see cref="Array2DPool{T}"/>, for a caller that
/// reads it once and lets it go (a plate solver's binned detection image, see
/// <see cref="Imaging.Image.DownsampleRented"/>). <see cref="Dispose"/> returns the planes, after which nothing
/// may read <see cref="Image"/>: the next renter is writing them.
/// </summary>
/// <remarks>
/// A class, not a struct, on purpose: a copied struct disposed twice would return the same plane to the
/// pool twice, and two later renters would then share it.
/// </remarks>
internal sealed class RentedImage(Image image, float[][,]? planes) : IDisposable
{
    private float[][,]? _planes = planes;

    /// <summary>The image, valid until <see cref="Dispose"/>.</summary>
    public Image Image { get; } = image;

    /// <summary>Returns the planes to the pool exactly once, however often it is called; a view with no
    /// rented planes (a factor of 1 is the source image itself) returns nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _planes, null) is { } rented)
        {
            foreach (var plane in rented)
            {
                Array2DPool<float>.Return(plane);
            }
        }
    }
}
