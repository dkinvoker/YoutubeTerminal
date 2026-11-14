using YoutubeTerminal.Graphics;

namespace YoutubeTerminal.Tests;

public class GraphicsTests
{
    private static int R(uint c) => (int)((c >> 16) & 0xFF);
    private static int G(uint c) => (int)((c >> 8) & 0xFF);
    private static int B(uint c) => (int)(c & 0xFF);

    private static Bitmap Gradient(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.Pixels[y * w + x] = Bitmap.Rgb(x * 255 / Math.Max(1, w - 1), y * 255 / Math.Max(1, h - 1), (x + y) * 255 / Math.Max(1, w + h - 2));
        return bmp;
    }

    // ---------------- Bitmap ----------------

    [Theory]
    [InlineData(1920, 1080, 100, 100, 100, 56)]
    [InlineData(1080, 1920, 100, 100, 56, 100)]
    [InlineData(640, 480, 320, 100, 133, 100)]
    [InlineData(0, 0, 50, 40, 50, 40)]
    public void FitSize_KeepsAspectRatio(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), Bitmap.FitSize(sw, sh, mw, mh));
    }

    [Fact]
    public void FromRgba_BlendsAlphaOverBackground()
    {
        byte[] rgba = [255, 0, 0, 255, /* */ 0, 0, 255, 0, /* */ 255, 255, 255, 128];
        var bmp = Bitmap.FromRgba(rgba, 3, 1, background: 0xFF000000);
        Assert.Equal(0xFFFF0000u, bmp.Pixels[0]);
        Assert.Equal(0xFF000000u, bmp.Pixels[1]);      // w pełni przezroczysty = tło
        Assert.InRange(R(bmp.Pixels[2]), 126, 130);    // pół na pół z czarnym
    }

    [Fact]
    public void CoverInto_HasExactSize()
    {
        var bmp = Gradient(160, 90).CoverInto(50, 50);
        Assert.Equal((50, 50), (bmp.Width, bmp.Height));
    }

    [Fact]
    public void Crop_CopiesRegion()
    {
        var src = Gradient(10, 10);
        var c = src.Crop(2, 3, 4, 5);
        Assert.Equal((4, 5), (c.Width, c.Height));
        Assert.Equal(src.Pixels[3 * 10 + 2], c.Pixels[0]);
        Assert.Equal(src.Pixels[7 * 10 + 5], c.Pixels[4 * 4 + 3]);
    }

    // ---------------- Scaler ----------------

    [Fact]
    public void Resize_UniformColorStaysUniform()
    {
        var src = new Bitmap(100, 50);
        src.Fill(Bitmap.Rgb(10, 200, 30));
        foreach (var (w, h) in new[] { (37, 19), (100, 50), (250, 120), (1, 1) })
        {
            var dst = Scaler.Resize(src, w, h);
            Assert.Equal((w, h), (dst.Width, dst.Height));
            Assert.All(dst.Pixels, p => Assert.Equal(Bitmap.Rgb(10, 200, 30), p));
        }
    }

    [Fact]
    public void Resize_DownscaleAveragesArea()
    {
        // szachownica 1-pikselowa: po zmniejszeniu 2× każdy piksel to średnia czerni i bieli
        var src = new Bitmap(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                src.Pixels[y * 8 + x] = (x + y) % 2 == 0 ? 0xFFFFFFFFu : 0xFF000000u;
        var dst = Scaler.Resize(src, 4, 4);
        Assert.All(dst.Pixels, p => Assert.InRange(R(p), 125, 130));
    }

    [Fact]
    public void Resize_SameSizeCopiesPixels()
    {
        var src = Gradient(31, 17);
        Assert.Equal(src.Pixels, Scaler.Resize(src, 31, 17).Pixels);
    }

    [Fact]
    public void Resize_LargeParallelPathMatchesColorRange()
    {
        // duży obraz uruchamia ścieżkę wielowątkową
        var src = Gradient(1280, 720);
        var dst = Scaler.Resize(src, 640, 360);
        Assert.InRange(R(dst.Pixels[0]), 0, 2);
        Assert.InRange(R(dst.Pixels[639]), 252, 255);
        Assert.InRange(G(dst.Pixels[359 * 640]), 252, 255);
    }

    // ---------------- Kolory NV12 ----------------

    private static unsafe uint[] Nv12(byte y, byte u, byte v, int w = 4, int h = 4, int cropX = 0)
    {
        var buf = new byte[w * h * 3 / 2];
        Array.Fill(buf, y, 0, w * h);
        for (int i = w * h; i < buf.Length; i += 2) { buf[i] = u; buf[i + 1] = v; }
        var dst = new uint[(w - cropX) * h];
        fixed (byte* p = buf)
            ColorConvert.Nv12ToArgb(p, p + w * h, w, cropX, 0, w - cropX, h, dst, bt709: true);
        return dst;
    }

    [Fact]
    public void Nv12_WhiteIsExactlyWhite()
    {
        Assert.All(Nv12(235, 128, 128), p => Assert.Equal(0xFFFFFFFFu, p));
    }

    [Fact]
    public void Nv12_BlackWhiteAndRed()
    {
        Assert.All(Nv12(16, 128, 128), p => Assert.Equal(0xFF000000u, p));
        Assert.All(Nv12(235, 128, 128), p => Assert.InRange(R(p), 254, 255));
        var red = Nv12(63, 102, 240)[0];        // czerwień w BT.709 (zakres ograniczony)
        Assert.InRange(R(red), 250, 255);
        Assert.InRange(G(red), 0, 5);
        Assert.InRange(B(red), 0, 5);
    }

    [Fact]
    public void Nv12_OddCropUsesCorrectChroma()
    {
        var px = Nv12(126, 128, 128, w: 6, h: 2, cropX: 1);
        Assert.Equal(10, px.Length);
        Assert.All(px, p => Assert.True(R(p) == G(p) && G(p) == B(p), "szary bez przesunięcia barwy"));
    }

    [Fact]
    public unsafe void Nv12_LargeFrameUsesParallelPathConsistently()
    {
        const int w = 640, h = 480;
        var buf = new byte[w * h * 3 / 2];
        new Random(5).NextBytes(buf);
        var a = new uint[w * h];
        fixed (byte* p = buf) ColorConvert.Nv12ToArgb(p, p + w * h, w, 0, 0, w, h, a, true);
        // ten sam wynik dla małego wycinka liczonego osobno (ścieżka jednowątkowa)
        var b = new uint[w * 2];
        fixed (byte* p = buf) ColorConvert.Nv12ToArgb(p, p + w * h, w, 0, 0, w, 2, b, true);
        Assert.Equal(a.AsSpan(0, w * 2).ToArray(), b);
    }

    // ---------------- Kwantyzacja ----------------

    [Fact]
    public void Adaptive_FewColorsAreExact()
    {
        uint[] px = [Bitmap.Rgb(255, 0, 0), Bitmap.Rgb(0, 255, 0), Bitmap.Rgb(0, 0, 255), Bitmap.Rgb(255, 0, 0)];
        var img = new IndexedImage();
        new Quantizer().QuantizeAdaptive(px, 4, 1, img);
        Assert.Equal(3, img.PaletteSize);
        for (int i = 0; i < 4; i++)
            Assert.Equal((int)(px[i] & 0xFFFFFF), img.Palette[img.Indices[i]]);
    }

    [Fact]
    public void Adaptive_ManyColorsRespectsLimitAndStaysClose()
    {
        var src = Gradient(64, 64);
        var img = new IndexedImage();
        new Quantizer().QuantizeAdaptive(src.Pixels, 64, 64, img, maxColors: 32);
        Assert.InRange(img.PaletteSize, 2, 32);
        double err = 0;
        for (int i = 0; i < src.Pixels.Length; i++)
        {
            Assert.True(img.Indices[i] < img.PaletteSize);
            int c = img.Palette[img.Indices[i]];
            uint p = src.Pixels[i];
            err += Math.Abs(R(p) - ((c >> 16) & 0xFF)) + Math.Abs(G(p) - ((c >> 8) & 0xFF)) + Math.Abs(B(p) - (c & 0xFF));
        }
        Assert.True(err / (src.Pixels.Length * 3) < 20, $"średni błąd {err / (src.Pixels.Length * 3):F1}");
    }

    [Fact]
    public void Fixed_MapsPureColors()
    {
        uint[] px = [0xFF000000, 0xFFFFFFFF];
        var img = new IndexedImage();
        new Quantizer().QuantizeFixed(px, 2, 1, img, dither: false);
        Assert.Equal(252, img.PaletteSize);
        Assert.Equal(0x000000, img.Palette[img.Indices[0]]);
        Assert.Equal(0xFFFFFF, img.Palette[img.Indices[1]]);
    }

    [Fact]
    public void Fixed_DitherProducesValidIndices()
    {
        var src = Gradient(32, 32);
        var img = new IndexedImage();
        new Quantizer().QuantizeFixed(src.Pixels, 32, 32, img, dither: true);
        Assert.All(img.Indices.Take(32 * 32), i => Assert.True(i < 252));
    }
}
