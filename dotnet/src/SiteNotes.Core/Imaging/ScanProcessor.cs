using SiteNotes.Core.Models;
using SkiaSharp;

namespace SiteNotes.Core.Imaging;

/// <summary>
/// Document-scanner image clean-up: auto contrast, grayscale and an adaptive black &amp; white
/// filter (Bradley thresholding) that removes shadows from paper photographed on site.
/// </summary>
public static class ScanProcessor
{
    /// <summary>Reads a camera photo, applies the filter and rotation, and saves a JPEG page.</summary>
    public static bool Process(string sourcePath, string destPath, ScanFilter filter, int rotateDegrees = 0, int maxDim = 2200)
    {
        using var src = ImageTools.LoadUpright(sourcePath, maxDim);
        if (src == null) return false;
        using var rotated = rotateDegrees % 360 != 0 ? ImageTools.Rotate(src, rotateDegrees) : src.Copy();
        using var result = Apply(rotated, filter);
        ImageTools.SaveJpeg(result, destPath, filter == ScanFilter.BlackWhite ? 70 : 82);
        return true;
    }

    public static SKBitmap Apply(SKBitmap src, ScanFilter filter) => filter switch
    {
        ScanFilter.Enhanced => AutoContrast(src, gray: false),
        ScanFilter.Grayscale => AutoContrast(src, gray: true),
        ScanFilter.BlackWhite => AdaptiveThreshold(src),
        _ => src.Copy(),
    };

    private static byte Luma(SKColor c) => (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);

    /// <summary>Stretches the 1st–99th percentile of brightness to the full range.</summary>
    public static SKBitmap AutoContrast(SKBitmap src, bool gray)
    {
        var px = src.Pixels;
        var hist = new int[256];
        foreach (var c in px) hist[Luma(c)]++;
        int lo = Percentile(hist, px.Length, 0.01), hi = Percentile(hist, px.Length, 0.99);
        if (hi - lo < 10) { lo = 0; hi = 255; }
        var scale = 255.0 / (hi - lo);
        var lut = new byte[256];
        for (var i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp((i - lo) * scale, 0, 255);

        var outPx = new SKColor[px.Length];
        for (var i = 0; i < px.Length; i++)
        {
            var c = px[i];
            outPx[i] = gray
                ? new SKColor(lut[Luma(c)], lut[Luma(c)], lut[Luma(c)])
                : new SKColor(lut[c.Red], lut[c.Green], lut[c.Blue]);
        }
        var dst = new SKBitmap(src.Width, src.Height);
        dst.Pixels = outPx;
        return dst;
    }

    private static int Percentile(int[] hist, int total, double p)
    {
        var target = total * p;
        long acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= target) return i;
        }
        return 255;
    }

    /// <summary>Each pixel is compared with the mean of its neighbourhood (window = 1/8 of the width),
    /// so uneven light and shadows don't turn into black blotches.</summary>
    public static SKBitmap AdaptiveThreshold(SKBitmap src, double sensitivity = 0.15)
    {
        int w = src.Width, h = src.Height;
        var px = src.Pixels;
        var gray = new byte[w * h];
        for (var i = 0; i < px.Length; i++) gray[i] = Luma(px[i]);

        var integral = new long[(w + 1) * (h + 1)];
        for (var y = 1; y <= h; y++)
        {
            long row = 0;
            for (var x = 1; x <= w; x++)
            {
                row += gray[(y - 1) * w + (x - 1)];
                integral[y * (w + 1) + x] = integral[(y - 1) * (w + 1) + x] + row;
            }
        }

        var s = Math.Max(8, w / 8) / 2;
        var outPx = new SKColor[w * h];
        for (var y = 0; y < h; y++)
        {
            int y1 = Math.Max(0, y - s), y2 = Math.Min(h - 1, y + s);
            for (var x = 0; x < w; x++)
            {
                int x1 = Math.Max(0, x - s), x2 = Math.Min(w - 1, x + s);
                var count = (x2 - x1 + 1) * (y2 - y1 + 1);
                var sum = integral[(y2 + 1) * (w + 1) + (x2 + 1)] - integral[y1 * (w + 1) + (x2 + 1)]
                          - integral[(y2 + 1) * (w + 1) + x1] + integral[y1 * (w + 1) + x1];
                var v = gray[y * w + x] * count < sum * (1.0 - sensitivity) ? (byte)0 : (byte)255;
                outPx[y * w + x] = new SKColor(v, v, v);
            }
        }
        var dst = new SKBitmap(w, h);
        dst.Pixels = outPx;
        return dst;
    }
}
