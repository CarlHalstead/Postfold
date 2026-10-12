using System;
using System.Buffers.Binary;
using System.IO;
using Postfold.Core;
using SkiaSharp;

namespace Postfold.Desktop;

/// <summary>Bounded full decode and re-encoding; metadata and unverified encoded bytes are never returned.</summary>
public static class PngImageNormalizer
{
    public static Outcome<byte[]> Normalize(byte[] bytes)
    {
        if (bytes.Length is < 33 or > 4194304 ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 4194304)
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }

        Outcome<byte[]> stripped = RemoveMetadata(bytes);
        if (!stripped.IsSuccess)
        {
            return stripped;
        }

        using MemoryStream source = new MemoryStream(stripped.Value!, false);
        using SKCodec? codec = SKCodec.Create(source);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1 ||
            codec.Info.Width != width || codec.Info.Height != height)
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }

        SKImageInfo info = new SKImageInfo((int)width, (int)height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            return Outcome<byte[]>.Fail(Failure.Validation);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData? encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded is null || encoded.Size > 4194304 ? Outcome<byte[]>.Fail(Failure.Validation) :
            Outcome<byte[]>.Success(encoded.ToArray());
    }

    private static Outcome<byte[]> RemoveMetadata(byte[] bytes)
    {
        using MemoryStream output = new MemoryStream();
        output.Write(bytes, 0, 8);
        int offset = 8;
        int chunks = 0;
        bool pixels = false;
        while (offset <= bytes.Length - 12 && ++chunks <= 1024)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12)
            {
                return Outcome<byte[]>.Fail(Failure.Validation);
            }

            ReadOnlySpan<byte> type = bytes.AsSpan(offset + 4, 4);
            bool header = type.SequenceEqual("IHDR"u8);
            bool data = type.SequenceEqual("IDAT"u8);
            bool end = type.SequenceEqual("IEND"u8);
            bool palette = type.SequenceEqual("PLTE"u8);
            bool transparency = type.SequenceEqual("tRNS"u8);
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8) ||
                (header && (offset != 8 || length != 13)) || (transparency && length > 256) ||
                (!header && !data && !end && !palette && (type[0] & 0x20) == 0))
            {
                return Outcome<byte[]>.Fail(Failure.Validation);
            }

            int size = checked((int)length + 12);
            if (header || data || end || palette || transparency)
            {
                output.Write(bytes, offset, size);
            }

            pixels |= data;
            offset += size;
            if (end)
            {
                return length == 0 && offset == bytes.Length && pixels ? Outcome<byte[]>.Success(output.ToArray()) :
                    Outcome<byte[]>.Fail(Failure.Validation);
            }
        }

        return Outcome<byte[]>.Fail(Failure.Validation);
    }
}
