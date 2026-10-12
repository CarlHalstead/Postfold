using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;
using SkiaSharp;

namespace Postfold.Foundation.Tests;

public sealed class ImageSecurityTests
{
    [Test]
    [TestCase("iCCP", true)]
    [TestCase("zTXt", true)]
    [TestCase("acTL", false)]
    [TestCase("ABCD", false)]
    public void Normalize_WhenExtraChunkContainsUntrustedMetadata_DiscardsMetadataOrRejectsUnsafeFormat(
        string chunk, bool allowed)
    {
        // Arrange
        using SKBitmap bitmap = new SKBitmap(1, 1);
        bitmap.Erase(SKColors.Blue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] original = encoded.ToArray();
        byte[] bytes = new byte[original.Length + 1036];
        original.AsSpan(0, 33).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), 1024);
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 37);
        bytes.AsSpan(41, 1024).Fill(255);
        original.AsSpan(33).CopyTo(bytes.AsSpan(1069));

        // Act
        Outcome<byte[]> result = PngImageNormalizer.Normalize(bytes);

        // Assert
        Assert.That(result.IsSuccess, Is.EqualTo(allowed));
        if (allowed)
        {
            using SKBitmap decoded = SKBitmap.Decode(result.Value!);
            Assert.That(decoded.GetPixel(0, 0), Is.EqualTo(SKColors.Blue));
            Assert.That(result.Value!.Length, Is.LessThan(1024));
        }
        else
        {
            Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        }
    }

    [Test]
    public void Normalize_WhenPngIsComplete_ReturnsDecodedStaticPixels()
    {
        // Arrange
        using SKBitmap original = new SKBitmap(2, 2);
        original.Erase(SKColors.Red);
        using SKImage image = SKImage.FromBitmap(original);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        // Act
        Outcome<byte[]> result = PngImageNormalizer.Normalize(encoded.ToArray());

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        using SKBitmap decoded = SKBitmap.Decode(result.Value!);
        Assert.That(decoded.Width, Is.EqualTo(2));
        Assert.That(decoded.GetPixel(1, 1), Is.EqualTo(SKColors.Red));
    }

    [Test]
    public void Normalize_WhenPngContainsOnlyHeader_RejectsBeforeReturningEncodedBytes()
    {
        // Arrange
        byte[] bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 1);

        // Act
        Outcome<byte[]> result = PngImageNormalizer.Normalize(bytes);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [Test]
    public void Normalize_WhenDeclaredDimensionsExceedBudget_RejectsBeforeDecoding()
    {
        // Arrange
        using SKBitmap bitmap = new SKBitmap(1, 1);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 4097);

        // Act
        Outcome<byte[]> result = PngImageNormalizer.Normalize(bytes);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }
}
