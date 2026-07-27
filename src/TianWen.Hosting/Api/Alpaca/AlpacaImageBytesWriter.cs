using System;
using System.Buffers.Binary;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Api.Alpaca
{
    /// <summary>
    /// Encodes a frame into the ASCOM <c>application/imagebytes</c> wire format -- the write counterpart
    /// of <c>AlpacaImageBytes.DecodeChannel</c>, which this node's own client uses to read it back.
    /// <para>
    /// <b>Why binary and not the JSON <c>imagearray</c>.</b> JSON encodes every pixel as a decimal-ASCII
    /// integer, an order of magnitude slower for a full frame; ImageBytes is a 44-byte little-endian
    /// header followed by raw pixels.
    /// </para>
    /// <para>
    /// <b>The wire-order trap.</b> ImageBytes is laid out <c>[Dimension1 = Width(X), Dimension2 =
    /// Height(Y)]</c> row-major (last index fastest) -- i.e. column-major in image terms, so the flat
    /// index of pixel <c>(x, y)</c> is <c>y + x*Height</c>. An <see cref="Image"/> channel is
    /// <c>[y, x]</c>. Getting this backwards produces a transposed frame that still decodes cleanly and
    /// looks almost plausible on a square sensor, which is why the round-trip test uses a non-square
    /// frame with an asymmetric marker.
    /// </para>
    /// </summary>
    public static class AlpacaImageBytesWriter
    {
        /// <summary>Header length for metadata version 1.</summary>
        public const int MetadataV1Length = 44;

        private const int MetadataVersion = 1;
        private const int RankTwoDimensional = 2;

        /// <summary>ASCOM <c>ImageArrayElementTypes.Int32</c>.</summary>
        private const int ElementTypeInt32 = 2;

        /// <summary>
        /// Encodes channel 0 of <paramref name="image"/> as raw ADU Int32 pixels.
        /// <para>
        /// Int32 rather than the narrower Int16 the sensor may actually use: ASCOM's canonical
        /// <c>ImageArray</c> element type is Int32, our decoder accepts it, and picking the widest safe
        /// type removes any chance of clipping a camera whose full scale exceeds 16 bits. The cost is
        /// bandwidth on a link that is already sending a full frame.
        /// </para>
        /// </summary>
        public static byte[] Encode(Image image)
        {
            var (_, width, height) = image.Shape;
            var payload = new byte[MetadataV1Length + (width * height * sizeof(int))];
            var span = payload.AsSpan();

            BinaryPrimitives.WriteInt32LittleEndian(span[0..], MetadataVersion);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0);                    // ErrorNumber
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], 0);                    // ClientTransactionID
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], 0);                   // ServerTransactionID
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], MetadataV1Length);    // DataStart
            BinaryPrimitives.WriteInt32LittleEndian(span[20..], ElementTypeInt32);    // ImageElementType
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], ElementTypeInt32);    // TransmissionElementType
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], RankTwoDimensional);
            BinaryPrimitives.WriteInt32LittleEndian(span[32..], width);               // Dimension1 = X
            BinaryPrimitives.WriteInt32LittleEndian(span[36..], height);              // Dimension2 = Y
            BinaryPrimitives.WriteInt32LittleEndian(span[40..], 0);                   // Dimension3 (unused, rank 2)

            var pixels = span[MetadataV1Length..];
            for (var x = 0; x < width; x++)
            {
                // Column-major on the wire: this whole column is contiguous.
                var columnStart = x * height * sizeof(int);
                for (var y = 0; y < height; y++)
                {
                    var value = image.GetChannelSpan(0)[(y * width) + x];
                    BinaryPrimitives.WriteInt32LittleEndian(
                        pixels[(columnStart + (y * sizeof(int)))..],
                        (int)MathF.Round(value));
                }
            }

            return payload;
        }

        /// <summary>
        /// The ImageBytes error shape: the same 44-byte header with a non-zero error number, followed by
        /// a UTF-8 message. Used instead of an HTTP error because the client decodes this body either
        /// way, and a bare status would tell it nothing.
        /// </summary>
        public static byte[] EncodeError(int errorNumber, string message)
        {
            var text = System.Text.Encoding.UTF8.GetBytes(message);
            var payload = new byte[MetadataV1Length + text.Length];
            var span = payload.AsSpan();

            BinaryPrimitives.WriteInt32LittleEndian(span[0..], MetadataVersion);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], errorNumber);
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], MetadataV1Length);
            text.CopyTo(span[MetadataV1Length..]);

            return payload;
        }
    }
}
