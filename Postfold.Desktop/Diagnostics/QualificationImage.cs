using SkiaSharp;

namespace Postfold.Desktop.Diagnostics;

internal static class QualificationImage
{
    internal static byte[] CreatePixel()
    {
        using SKBitmap bitmap = new SKBitmap(1, 1);
        bitmap.Erase(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
