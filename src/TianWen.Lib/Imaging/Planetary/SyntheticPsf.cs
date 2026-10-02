using System;
using System.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What every frame of a synthetic capture shares, at the head of its PSF file (<see cref="SyntheticPsfFile"/>): the fine grid the
/// PSFs are on, the scatter that takes its share of the light from each, the camera, and the perfect telescope's PSF that the
/// capture's truth is rendered through.
/// </summary>
/// <param name="PsfGrid">The PSFs' side, in fine samples.</param>
/// <param name="Oversample">Fine samples to a detector pixel.</param>
/// <param name="ArcsecPerPixel">The detector's scale.</param>
/// <param name="WavelengthM">The wavelength the frames are imaged at.</param>
/// <param name="ScatterFraction">The share of the light the telescope scatters wide.</param>
/// <param name="ScatterCoreArcsec">The scatter kernel's core, (1 + (r / core)^2)^(-3/2).</param>
/// <param name="OffsetAdu">The camera's offset, the sky's level.</param>
/// <param name="ReadNoiseAdu">The read noise.</param>
/// <param name="ElectronsPerAdu">The gain, which sets the shot noise.</param>
/// <param name="Diffraction">The perfect telescope's PSF on the same grid, unit-sum.</param>
public sealed record SyntheticPsfHeader(int PsfGrid, int Oversample, double ArcsecPerPixel, double WavelengthM, double ScatterFraction, double ScatterCoreArcsec,
    double OffsetAdu, double ReadNoiseAdu, double ElectronsPerAdu, double[] Diffraction)
{
    /// <summary>The header of a capture made with <paramref name="options"/> at <paramref name="arcsecPerPixel"/>.</summary>
    public static SyntheticPsfHeader For(DegradeOptions options, double arcsecPerPixel)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SyntheticPsfHeader(PlanetaryDegrade.PsfGrid, PlanetaryDegrade.OversampleFor(arcsecPerPixel, options.Pupil.DiameterM, options.WavelengthM), arcsecPerPixel,
            options.WavelengthM, options.ScatterFraction, options.ScatterCoreArcsec, options.OffsetAdu, options.ReadNoiseAdu, options.ElectronsPerAdu,
            PlanetaryDegrade.DiffractionPsf(options, arcsecPerPixel));
    }
}

/// <summary>
/// A synthetic capture's PSFs, one per frame, beside it as <c>&lt;capture&gt;.psf</c> (docs/plans/planetary-restoration.md, R8 part 1):
/// <c>TWPSF01</c>, the header with the diffraction PSF, then each frame's shift, brightness and PSF, little-endian, the PSF as floats.
/// </summary>
public static class SyntheticPsfFile
{
    private static ReadOnlySpan<byte> Magic => "TWPSF01\0"u8;

    /// <summary>The PSF file beside <paramref name="capture"/>.</summary>
    public static string PathFor(string capture) => Path.ChangeExtension(capture, ".psf");

    /// <summary>Writes a header, then the frames' PSFs as they come, in frame order.</summary>
    public sealed class Writer : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly int _samples;

        /// <summary>A writer to <paramref name="path"/>, replacing it, the header first.</summary>
        public Writer(string path, SyntheticPsfHeader header)
        {
            ArgumentNullException.ThrowIfNull(header);
            _samples = header.PsfGrid * header.PsfGrid;
            ArgumentOutOfRangeException.ThrowIfNotEqual(header.Diffraction.Length, _samples);
            _writer = new BinaryWriter(File.Create(path));
            _writer.Write(Magic);
            _writer.Write(header.PsfGrid);
            _writer.Write(header.Oversample);
            _writer.Write(header.ArcsecPerPixel);
            _writer.Write(header.WavelengthM);
            _writer.Write(header.ScatterFraction);
            _writer.Write(header.ScatterCoreArcsec);
            _writer.Write(header.OffsetAdu);
            _writer.Write(header.ReadNoiseAdu);
            _writer.Write(header.ElectronsPerAdu);
            foreach (var v in header.Diffraction)
            {
                _writer.Write(v);
            }
        }

        /// <summary>The next frame's PSF, shift and brightness.</summary>
        public void Append(SyntheticFrameOptics optics)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(optics.Psf.Length, _samples);
            _writer.Write(optics.ShiftX);
            _writer.Write(optics.ShiftY);
            _writer.Write(optics.Brightness);
            foreach (var v in optics.Psf)
            {
                _writer.Write((float)v);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => _writer.Dispose();
    }

    /// <summary>Reads the header, then the frames in order, one at a time: a capture's PSFs are too many to hold at once.</summary>
    public sealed class Reader : IDisposable
    {
        private readonly BinaryReader _reader;

        private Reader(BinaryReader reader, SyntheticPsfHeader header)
        {
            (_reader, Header) = (reader, header);
        }

        /// <summary>What every frame shares.</summary>
        public SyntheticPsfHeader Header { get; }

        /// <summary>A reader of <paramref name="path"/>; null when it is not a PSF file.</summary>
        public static Reader? Open(string path)
        {
            // Disposed on every path but the one that hands it to the reader returned, a short file's EndOfStreamException included.
            BinaryReader? reader = new BinaryReader(File.OpenRead(path));
            try
            {
                if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                {
                    return null;
                }
                var grid = reader.ReadInt32();
                var oversample = reader.ReadInt32();
                var (scale, wavelength, scatter, core) = (reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                var (offset, readNoise, gain) = (reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                var diffraction = new double[grid * grid];
                for (var i = 0; i < diffraction.Length; i++)
                {
                    diffraction[i] = reader.ReadDouble();
                }
                var opened = new Reader(reader, new SyntheticPsfHeader(grid, oversample, scale, wavelength, scatter, core, offset, readNoise, gain, diffraction));
                reader = null;
                return opened;
            }
            finally
            {
                reader?.Dispose();
            }
        }

        /// <summary>
        /// The next frame into <paramref name="psf"/> (the header's grid squared), with its shift and brightness; false once every frame
        /// has been read.
        /// </summary>
        public bool TryRead(double[] psf, out double shiftX, out double shiftY, out double brightness)
        {
            ArgumentNullException.ThrowIfNull(psf);
            ArgumentOutOfRangeException.ThrowIfNotEqual(psf.Length, Header.PsfGrid * Header.PsfGrid);
            var frameBytes = 24 + (4L * psf.Length);
            if (_reader.BaseStream.Length - _reader.BaseStream.Position < frameBytes)
            {
                (shiftX, shiftY, brightness) = (double.NaN, double.NaN, double.NaN);
                return false;
            }
            (shiftX, shiftY, brightness) = (_reader.ReadDouble(), _reader.ReadDouble(), _reader.ReadDouble());
            for (var i = 0; i < psf.Length; i++)
            {
                psf[i] = _reader.ReadSingle();
            }
            return true;
        }

        /// <inheritdoc/>
        public void Dispose() => _reader.Dispose();
    }
}
