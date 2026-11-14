namespace YoutubeTerminal.Graphics;

/// <summary>
/// Prosta bitmapa 32-bitowa. Każdy piksel to uint w formacie 0xAARRGGBB
/// (w pamięci: B, G, R, A — zgodne z MFVideoFormat_RGB32).
/// </summary>
internal sealed class Bitmap
{
    public readonly int Width;
    public readonly int Height;
    public readonly uint[] Pixels;

    public Bitmap(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new uint[Width * Height];
    }

    public Bitmap(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public void Fill(uint argb) => Array.Fill(Pixels, argb);

    public static uint Rgb(int r, int g, int b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    /// <summary>Tworzy bitmapę z danych RGBA (np. z dekodera obrazu).</summary>
    public static Bitmap FromRgba(ReadOnlySpan<byte> rgba, int width, int height, uint background = 0xFF000000)
    {
        var bmp = new Bitmap(width, height);
        var px = bmp.Pixels;
        int bgR = (int)((background >> 16) & 0xFF), bgG = (int)((background >> 8) & 0xFF), bgB = (int)(background & 0xFF);
        for (int i = 0, j = 0; i < px.Length; i++, j += 4)
        {
            int r = rgba[j], g = rgba[j + 1], b = rgba[j + 2], a = rgba[j + 3];
            if (a != 255)
            {
                r = (r * a + bgR * (255 - a)) / 255;
                g = (g * a + bgG * (255 - a)) / 255;
                b = (b * a + bgB * (255 - a)) / 255;
            }
            px[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
        return bmp;
    }

    /// <summary>Zwraca nową bitmapę przeskalowaną tak, by zmieściła się w podanym prostokącie z zachowaniem proporcji.</summary>
    public Bitmap FitInto(int maxWidth, int maxHeight)
    {
        var (w, h) = FitSize(Width, Height, maxWidth, maxHeight);
        return Scaler.Resize(this, w, h);
    }

    /// <summary>Skaluje i przycina do dokładnie podanego rozmiaru (jak object-fit: cover).</summary>
    public Bitmap CoverInto(int width, int height)
    {
        double scale = Math.Max((double)width / Width, (double)height / Height);
        int cropW = Math.Clamp((int)Math.Round(width / scale), 1, Width);
        int cropH = Math.Clamp((int)Math.Round(height / scale), 1, Height);
        int x0 = (Width - cropW) / 2, y0 = (Height - cropH) / 2;
        var cropped = Crop(x0, y0, cropW, cropH);
        return Scaler.Resize(cropped, width, height);
    }

    public Bitmap Crop(int x, int y, int w, int h)
    {
        if (x == 0 && y == 0 && w == Width && h == Height) return this;
        var bmp = new Bitmap(w, h);
        for (int row = 0; row < h; row++)
            Array.Copy(Pixels, (y + row) * Width + x, bmp.Pixels, row * w, w);
        return bmp;
    }

    public static (int W, int H) FitSize(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= 0 || srcH <= 0) return (Math.Max(1, maxW), Math.Max(1, maxH));
        double s = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        int w = Math.Max(1, (int)Math.Floor(srcW * s));
        int h = Math.Max(1, (int)Math.Floor(srcH * s));
        return (Math.Min(w, maxW), Math.Min(h, maxH));
    }
}
