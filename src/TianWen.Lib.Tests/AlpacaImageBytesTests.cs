using System;
using System.Buffers.Binary;
using System.Text;
using Shouldly;
using TianWen.Lib.Devices.Alpaca;
using Xunit;

namespace TianWen.Lib.Tests;

public class AlpacaImageBytesTests
{
    // ASCOM ImageArrayElementTypes wire values used in these payloads.
    private const int Int16 = 1, Int32 = 2, UInt16 = 8;

    /// <summary>
    /// Builds an ImageBytes payload (44-byte ArrayMetadataV1 header + pixels) the way an Alpaca
    /// server would: pixels in ASCOM column-major order, flat index of (x, y) = y + x*height.
    /// </summary>
    private static byte[] BuildPayload(int width, int height, int transmissionType, Func<int, int, long> valueAt,
        int imageType = -1, int errorNumber = 0, int rank = 2, string errorMessage = "")
    {
        if (imageType < 0) imageType = transmissionType;
        var elemSize = transmissionType switch { Int16 or UInt16 => 2, Int32 => 4, _ => throw new ArgumentException("unsupported", nameof(transmissionType)) };

        byte[] body;
        if (errorNumber != 0)
        {
            body = Encoding.UTF8.GetBytes(errorMessage);
        }
        else
        {
            body = new byte[width * height * elemSize];
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    var off = (y + x * height) * elemSize; // ASCOM flat order, Y fastest
                    var v = valueAt(x, y);
                    switch (transmissionType)
                    {
                        case Int16: BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(off), (short)v); break;
                        case UInt16: BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off), (ushort)v); break;
                        case Int32: BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(off), (int)v); break;
                    }
                }
            }
        }

        var payload = new byte[44 + body.Length];
        var h = payload.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h[0..], 1);                 // MetadataVersion
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], errorNumber);       // ErrorNumber
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], 1);                 // ClientTransactionID
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], 1);                // ServerTransactionID
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 44);              // DataStart
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], imageType);        // ImageElementType
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], transmissionType); // TransmissionElementType
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], rank);             // Rank
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], width);            // Dimension1 = X / width
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], height);           // Dimension2 = Y / height
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], 0);               // Dimension3
        body.CopyTo(payload, 44);
        return payload;
    }

    [Fact]
    public void DecodeChannel_TransposesAscomColumnMajorIntoRowMajorHeightWidth()
    {
        // 3 wide x 2 high, asymmetric pattern so a transpose bug can't pass: value = x*10 + y.
        var payload = BuildPayload(width: 3, height: 2, transmissionType: Int16, valueAt: (x, y) => x * 10 + y);

        var channel = AlpacaImageBytes.DecodeChannel(payload);

        channel.Width.ShouldBe(3);
        channel.Height.ShouldBe(2);
        for (var x = 0; x < 3; x++)
        {
            for (var y = 0; y < 2; y++)
            {
                channel[y, x].ShouldBe((float)(x * 10 + y)); // pixel (x, y) must land at [y, x]
            }
        }
        channel.MinValue.ShouldBe(0f);
        channel.MaxValue.ShouldBe(21f);
    }

    [Fact]
    public void DecodeChannel_UInt16_DecodesFullRange()
    {
        var payload = BuildPayload(2, 2, UInt16, (x, y) => x == 1 && y == 1 ? 60000 : 100);

        var channel = AlpacaImageBytes.DecodeChannel(payload);

        channel[1, 1].ShouldBe(60000f);
        channel.MaxValue.ShouldBe(60000f);
        channel.MinValue.ShouldBe(100f);
    }

    [Fact]
    public void DecodeChannel_TransmissionTypeSmallerThanImageType_DecodesViaTransmissionType()
    {
        // Server declares an Int32 image but transmits Int16 to save bandwidth.
        var payload = BuildPayload(2, 2, transmissionType: Int16, valueAt: (x, y) => x + y, imageType: Int32);

        var channel = AlpacaImageBytes.DecodeChannel(payload);

        channel.Width.ShouldBe(2);
        channel[1, 1].ShouldBe(2f);
    }

    [Fact]
    public void DecodeChannel_NonZeroErrorNumber_ThrowsAlpacaExceptionWithMessage()
    {
        var payload = BuildPayload(0, 0, Int16, (_, _) => 0, errorNumber: 1025, errorMessage: "Camera not connected");

        var ex = Should.Throw<AlpacaException>(() => AlpacaImageBytes.DecodeChannel(payload));
        ex.ErrorNumber.ShouldBe(1025);
        ex.Message.ShouldBe("Camera not connected");
    }

    [Fact]
    public void DecodeChannel_Rank3_ThrowsNotSupported()
        => Should.Throw<NotSupportedException>(() => AlpacaImageBytes.DecodeChannel(BuildPayload(2, 2, Int16, (_, _) => 1, rank: 3)));

    [Fact]
    public void DecodeChannel_TruncatedHeader_ThrowsNotSupported()
        => Should.Throw<NotSupportedException>(() => AlpacaImageBytes.DecodeChannel(new byte[10]));

    [Fact]
    public void DecodeChannel_PixelDataTooShort_ThrowsNotSupported()
    {
        var full = BuildPayload(4, 4, Int16, (_, _) => 1);
        var truncated = full.AsSpan(0, 44 + 8).ToArray(); // only 4 of 16 pixels present

        Should.Throw<NotSupportedException>(() => AlpacaImageBytes.DecodeChannel(truncated));
    }
}
