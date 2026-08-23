using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A disposable borrow of an <see cref="Imaging.Image"/> -- the token side of the frame-ownership
/// policy. <see cref="Imaging.Image.TryLease"/> hands one out; disposing it is the borrower's whole
/// obligation, spent exactly once however many copies of the struct exist.
/// </summary>
/// <remarks>
/// <para><b>Why a token rather than a returned <see cref="Imaging.Image"/>.</b> A shared refcount can
/// DETECT a double-release but cannot prevent one: the offending call is byte-identical to a
/// legitimate last release, so <see cref="ChannelBuffer"/> can only throw at the NEXT, innocent
/// release. A token is 1:1 with a claim -- disposing it ends this borrower's whole claim,
/// unconditionally, and the frame's fate is computed from the surviving claims rather than asserted
/// by whoever called last. It also puts the obligation where the language can carry it: a borrowed
/// frame offers no <c>Release</c> to call by mistake, and <c>using</c> makes the common shape the
/// correct one. (NOT the analyzers: CA2000 ignores value-type disposables -- measured with a
/// forgotten lease beside a forgotten <c>FileStream</c>, and only the stream fired -- so a dropped
/// lease is caught by the DEBUG <see cref="ChannelBufferLeakTracker"/>, not at compile time.)</para>
/// <para>The wrapped image is DISTINCT from the source frame -- it shares the pixel planes and holds
/// its own buffer refs -- so disposing a lease can never mark the source released, and repeated
/// borrow cycles against the same published frame all succeed. <c>default(ImageLease)</c> is inert:
/// <see cref="Dispose"/> is a no-op and <see cref="Image"/> throws, because reading an empty lease
/// is a bug at the call site, not a race.</para>
/// <para>This is the BORROW token only. An owner spends ownership with
/// <see cref="Imaging.Image.Release"/> directly (a no-op for self-owned frames, which stay
/// readable) -- see the frame-ownership notes on <see cref="Imaging.Image"/>.</para>
/// </remarks>
public readonly struct ImageLease : IDisposable
{
    private readonly Image? _leased;

    internal ImageLease(Image leased) => _leased = leased;

    /// <summary>The borrowed frame, valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The lease is empty: <see cref="Imaging.Image.TryLease"/> answered <see langword="false"/>, or
    /// this is <c>default(ImageLease)</c>.
    /// </exception>
    public Image Image => _leased ?? throw new InvalidOperationException(
        "Empty lease: TryLease answered false (or this is default(ImageLease)), so there is no frame to read.");

    /// <summary>
    /// Gives the borrow back. Safe on an empty lease, and idempotent per lease: the leased image's
    /// own <see cref="Imaging.Image.Release"/> is one-shot, so a copied struct disposing again is a
    /// no-op rather than a second spend.
    /// </summary>
    public void Dispose() => _leased?.Release();
}
