namespace YoutubeTerminal.Graphics;

/// <summary>Konwersja NV12 (YUV 4:2:0) do ARGB z tablicami przeglądowymi.</summary>
internal static unsafe class ColorConvert
{
    private static readonly int[] YTab = new int[256];
    private static readonly int[] Rv601 = new int[256], Gu601 = new int[256], Gv601 = new int[256], Bu601 = new int[256];
    private static readonly int[] Rv709 = new int[256], Gu709 = new int[256], Gv709 = new int[256], Bu709 = new int[256];
    private static readonly byte[] Clamp = new byte[1024 * 3];

    static ColorConvert()
    {
        // Pełna precyzja współczynników (zakres ograniczony: Y 16–235, C 16–240).
        // +512 w tablicy Y zaokrągla wynik przesunięcia >>10 zamiast go obcinać (biel = 255, nie 254).
        const double ys = 255.0 / 219.0;
        for (int i = 0; i < 256; i++)
        {
            YTab[i] = (int)Math.Round(ys * (i - 16) * 1024) + 512;
            int c = i - 128;
            Rv601[i] = (int)Math.Round(1.596027 * c * 1024);
            Gu601[i] = (int)Math.Round(-0.391762 * c * 1024);
            Gv601[i] = (int)Math.Round(-0.812968 * c * 1024);
            Bu601[i] = (int)Math.Round(2.017232 * c * 1024);
            Rv709[i] = (int)Math.Round(1.792741 * c * 1024);
            Gu709[i] = (int)Math.Round(-0.213249 * c * 1024);
            Gv709[i] = (int)Math.Round(-0.532909 * c * 1024);
            Bu709[i] = (int)Math.Round(2.112402 * c * 1024);
        }
        for (int i = 0; i < Clamp.Length; i++)
            Clamp[i] = (byte)Math.Clamp(i - 1024, 0, 255);
    }

    /// <summary>
    /// Konwertuje wycinek ramki NV12 do bufora ARGB o rozmiarze width x height.
    /// </summary>
    public static void Nv12ToArgb(byte* yPlane, byte* uvPlane, int stride, int cropX, int cropY, int width, int height, uint[] dst, bool bt709)
    {
        var rv = bt709 ? Rv709 : Rv601;
        var gu = bt709 ? Gu709 : Gu601;
        var gv = bt709 ? Gv709 : Gv601;
        var bu = bt709 ? Bu709 : Bu601;

        int threads = width * height >= 200_000 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : 1;
        if (threads > 1)
        {
            byte* yp = yPlane, uvp = uvPlane;
            Parallel.For(0, threads, t =>
            {
                int y0 = height * t / threads, y1 = height * (t + 1) / threads;
                ConvertRows(yp, uvp, stride, cropX, cropY, width, y0, y1, dst, rv, gu, gv, bu);
            });
        }
        else ConvertRows(yPlane, uvPlane, stride, cropX, cropY, width, 0, height, dst, rv, gu, gv, bu);
    }

    private static void ConvertRows(byte* yPlane, byte* uvPlane, int stride, int cropX, int cropY, int width, int yStart, int yEnd,
        uint[] dst, int[] rv, int[] gu, int[] gv, int[] bu)
    {
        fixed (uint* d0 = dst)
        fixed (int* yt = YTab, prv = rv, pgu = gu, pgv = gv, pbu = bu)
        fixed (byte* cl = Clamp)
        {
            byte* clamp = cl + 1024;
            for (int y = yStart; y < yEnd; y++)
            {
                byte* yr = yPlane + (long)(y + cropY) * stride + cropX;
                byte* uvr = uvPlane + (long)((y + cropY) >> 1) * stride + (cropX & ~1);
                uint* dr = d0 + (long)y * width;
                for (int x = 0; x < width; x++)
                {
                    int Y = yt[yr[x]];
                    int ux = ((x + (cropX & 1)) & ~1);
                    int U = uvr[ux], V = uvr[ux + 1];
                    int r = (Y + prv[V]) >> 10;
                    int g = (Y + pgu[U] + pgv[V]) >> 10;
                    int b = (Y + pbu[U]) >> 10;
                    if (r < -1024) r = -1024; else if (r > 2047) r = 2047;
                    if (g < -1024) g = -1024; else if (g > 2047) g = 2047;
                    if (b < -1024) b = -1024; else if (b > 2047) b = 2047;
                    dr[x] = 0xFF000000u | ((uint)clamp[r] << 16) | ((uint)clamp[g] << 8) | clamp[b];
                }
            }
        }
    }
}
