using SkiaSharp;

namespace SiteNotes.Core.Imaging;

public static class ImageTools
{
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Decodes a photo, scaled down to at most <paramref name="maxDim"/> pixels on its long
    /// side and turned upright using the camera's EXIF orientation. Returns null if it can't be read.</summary>
    public static SKBitmap? LoadUpright(string path, int maxDim = 2000)
    {
        if (!File.Exists(path)) return null;
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;

        var info = codec.Info;
        var scale = Math.Min(1f, (float)maxDim / Math.Max(info.Width, info.Height));
        var dims = codec.GetScaledDimensions(scale); // JPEG decodes at 1/2, 1/4, 1/8 very cheaply
        var bmp = new SKBitmap(new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bmp.Dispose();
            return null;
        }

        if (Math.Max(bmp.Width, bmp.Height) > maxDim)
        {
            var s = (float)maxDim / Math.Max(bmp.Width, bmp.Height);
            var resized = bmp.Resize(new SKImageInfo((int)(bmp.Width * s), (int)(bmp.Height * s)), Smooth);
            bmp.Dispose();
            bmp = resized;
        }

        var degrees = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0
        };
        if (degrees == 0) return bmp;
        var rotated = Rotate(bmp, degrees);
        bmp.Dispose();
        return rotated;
    }

    public static SKBitmap Rotate(SKBitmap src, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        var swap = degrees is 90 or 270;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var c = new SKCanvas(dst);
        c.Translate(dst.Width / 2f, dst.Height / 2f);
        c.RotateDegrees(degrees);
        c.Translate(-src.Width / 2f, -src.Height / 2f);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>JPEG-encodes a bitmap. PDFs embed these bytes as-is, which keeps reports small.</summary>
    public static SKData EncodeJpeg(SKBitmap bmp, int quality = 80)
    {
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, quality);
    }

    public static void SaveJpeg(SKBitmap bmp, string path, int quality = 85)
    {
        using var data = EncodeJpeg(bmp, quality);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Loads a photo ready to place in a PDF (upright, resized, JPEG-backed).</summary>
    public static SKImage? LoadForPdf(string path, int maxDim = 1600, int quality = 78)
    {
        using var bmp = LoadUpright(path, maxDim);
        if (bmp == null) return null;
        var data = EncodeJpeg(bmp, quality);
        return SKImage.FromEncodedData(data);
    }

    /// <summary>Makes a small square-ish thumbnail file (used for lists and sync previews).</summary>
    public static bool MakeThumbnail(string src, string dest, int maxDim = 360)
    {
        using var bmp = LoadUpright(src, maxDim);
        if (bmp == null) return false;
        SaveJpeg(bmp, dest, 75);
        return true;
    }
}
