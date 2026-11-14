namespace YoutubeTerminal.Graphics;

/// <summary>Wynik kwantyzacji: paleta (0xRRGGBB) i indeksy pikseli.</summary>
internal sealed class IndexedImage
{
    public int Width, Height;
    public byte[] Indices = [];
    public int[] Palette = [];
    public int PaletteSize;
}

/// <summary>
/// Kwantyzacja kolorów do ≤256 barw:
///  • adaptacyjna (median-cut na histogramie 15-bit) — najlepsza jakość, bez ditheringu,
///  • stała paleta 6×7×6 z ditheringiem Bayera — stabilna i szybka.
/// </summary>
internal sealed class Quantizer
{
    private readonly int[] _hist = new int[32768];
    private readonly byte[] _map = new byte[32768];
    private readonly int[] _colors = new int[32768];
    private readonly long[] _sumR = new long[256], _sumG = new long[256], _sumB = new long[256], _cnt = new long[256];

    private static readonly byte[] FixedLut = BuildFixedLut();
    private static readonly int[] FixedPalette = BuildFixedPalette();
    private static readonly int[] Bayer8 =
    [
        0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    ];

    private static int[] BuildFixedPalette()
    {
        var p = new int[6 * 7 * 6];
        int i = 0;
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 7; g++)
                for (int b = 0; b < 6; b++)
                    p[i++] = (r * 255 / 5 << 16) | (g * 255 / 6 << 8) | (b * 255 / 5);
        return p;
    }

    private static byte[] BuildFixedLut()
    {
        // LUT 5-5-5 → indeks w palecie 6×7×6
        var lut = new byte[32768];
        for (int r = 0; r < 32; r++)
            for (int g = 0; g < 32; g++)
                for (int b = 0; b < 32; b++)
                {
                    int ri = (r * 255 / 31 * 5 + 127) / 255;
                    int gi = (g * 255 / 31 * 6 + 127) / 255;
                    int bi = (b * 255 / 31 * 5 + 127) / 255;
                    lut[(r << 10) | (g << 5) | b] = (byte)(ri * 42 + gi * 6 + bi);
                }
        return lut;
    }

    public void QuantizeFixed(uint[] px, int w, int h, IndexedImage dst, bool dither)
    {
        Prepare(dst, w, h);
        Array.Copy(FixedPalette, dst.Palette, FixedPalette.Length);
        dst.PaletteSize = FixedPalette.Length;
        var idx = dst.Indices;
        var lut = FixedLut;
        if (!dither)
        {
            for (int i = 0; i < w * h; i++)
            {
                uint c = px[i];
                idx[i] = lut[((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F)];
            }
            return;
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int by = (y & 7) * 8;
            for (int x = 0; x < w; x++)
            {
                uint c = px[row + x];
                int d = Bayer8[by + (x & 7)] - 32; // -32..31
                int r = (int)((c >> 16) & 0xFF) + d * 51 / 64;
                int g = (int)((c >> 8) & 0xFF) + d * 42 / 64;
                int b = (int)(c & 0xFF) + d * 51 / 64;
                r = r < 0 ? 0 : r > 255 ? 255 : r;
                g = g < 0 ? 0 : g > 255 ? 255 : g;
                b = b < 0 ? 0 : b > 255 ? 255 : b;
                idx[row + x] = lut[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
            }
        }
    }

    /// <summary>Paleta adaptacyjna (median-cut).</summary>
    public void QuantizeAdaptive(uint[] px, int w, int h, IndexedImage dst, int maxColors = 255)
    {
        Prepare(dst, w, h);
        int n = w * h;
        Array.Clear(_hist);
        for (int i = 0; i < n; i++)
        {
            uint c = px[i];
            _hist[((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F)]++;
        }

        int count = 0;
        for (int i = 0; i < 32768; i++)
            if (_hist[i] != 0) _colors[count++] = i;

        // Pudełka: zakresy w tablicy _colors + zapamiętane statystyki (liczone tylko dla nowych pudełek).
        Span<int> boxStart = stackalloc int[256];
        Span<int> boxEnd = stackalloc int[256];
        Span<long> boxScore = stackalloc long[256];
        Span<int> boxAxis = stackalloc int[256];
        Span<long> boxPix = stackalloc long[256];
        int boxes = 1;
        boxStart[0] = 0;
        boxEnd[0] = count;

        if (count > maxColors)
        {
            ComputeBox(0, boxStart, boxEnd, boxScore, boxAxis, boxPix);
            while (boxes < maxColors)
            {
                int best = -1;
                long bestScore = 0;
                for (int b = 0; b < boxes; b++)
                    if (boxScore[b] > bestScore) { bestScore = boxScore[b]; best = b; }
                if (best < 0) break;

                int bs = boxStart[best], be = boxEnd[best];
                SortByAxis(bs, be, boxAxis[best]);
                // Podział w medianie liczby pikseli
                long half = boxPix[best] / 2, acc = 0;
                int split = bs + 1;
                for (int k = bs; k < be - 1; k++)
                {
                    acc += _hist[_colors[k]];
                    if (acc >= half) { split = k + 1; break; }
                }
                boxEnd[best] = split;
                boxStart[boxes] = split;
                boxEnd[boxes] = be;
                ComputeBox(best, boxStart, boxEnd, boxScore, boxAxis, boxPix);
                ComputeBox(boxes, boxStart, boxEnd, boxScore, boxAxis, boxPix);
                boxes++;
            }
        }

        Array.Clear(_sumR); Array.Clear(_sumG); Array.Clear(_sumB); Array.Clear(_cnt);
        if (count <= maxColors)
        {
            // Mało kolorów — każdy kolor dostaje własny wpis.
            for (int k = 0; k < count; k++)
            {
                int c = _colors[k];
                _map[c] = (byte)k;
                dst.Palette[k] = Expand(c);
            }
            dst.PaletteSize = Math.Max(1, count);
        }
        else
        {
            for (int b = 0; b < boxes; b++)
            {
                for (int k = boxStart[b]; k < boxEnd[b]; k++)
                {
                    int c = _colors[k];
                    _map[c] = (byte)b;
                    long hcount = _hist[c];
                    _sumR[b] += (c >> 10) * hcount;
                    _sumG[b] += ((c >> 5) & 31) * hcount;
                    _sumB[b] += (c & 31) * hcount;
                    _cnt[b] += hcount;
                }
                long cn = Math.Max(1, _cnt[b]);
                int r = (int)((_sumR[b] * 255 + cn * 15) / (cn * 31));
                int g = (int)((_sumG[b] * 255 + cn * 15) / (cn * 31));
                int bl = (int)((_sumB[b] * 255 + cn * 15) / (cn * 31));
                dst.Palette[b] = (Math.Min(255, r) << 16) | (Math.Min(255, g) << 8) | Math.Min(255, bl);
            }
            dst.PaletteSize = boxes;
        }

        var idx = dst.Indices;
        var map = _map;
        for (int i = 0; i < n; i++)
        {
            uint c = px[i];
            idx[i] = map[((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F)];
        }
    }

    private void ComputeBox(int b, Span<int> start, Span<int> end, Span<long> score, Span<int> axis, Span<long> pixels)
    {
        int s = start[b], e = end[b];
        int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
        long pix = 0;
        var colors = _colors;
        var hist = _hist;
        for (int k = s; k < e; k++)
        {
            int c = colors[k];
            int r = c >> 10, g = (c >> 5) & 31, bl = c & 31;
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (bl < bMin) bMin = bl;
            if (bl > bMax) bMax = bl;
            pix += hist[c];
        }
        pixels[b] = pix;
        int rr = (rMax - rMin) * 3, gr = (gMax - gMin) * 4, br = (bMax - bMin) * 2; // wagi percepcyjne
        int range = Math.Max(rr, Math.Max(gr, br));
        if (e - s < 2 || range == 0) { score[b] = 0; return; }
        score[b] = range * (long)Math.Sqrt(pix + 1);
        axis[b] = range == gr ? 1 : range == rr ? 0 : 2;
    }

    private int[] _sortTmp = new int[32768];

    private static int Expand(int c)
    {
        int r = (c >> 10) * 255 / 31, g = ((c >> 5) & 31) * 255 / 31, b = (c & 31) * 255 / 31;
        return (r << 16) | (g << 8) | b;
    }

    private void SortByAxis(int start, int end, int axis)
    {
        int shift = axis == 0 ? 10 : axis == 1 ? 5 : 0;
        // Sortowanie przez zliczanie (32 kubełki)
        Span<int> counts = stackalloc int[33];
        for (int k = start; k < end; k++) counts[((_colors[k] >> shift) & 31) + 1]++;
        for (int i = 1; i < 33; i++) counts[i] += counts[i - 1];
        var tmp = _sortTmp;
        for (int k = start; k < end; k++)
        {
            int v = (_colors[k] >> shift) & 31;
            tmp[counts[v]++] = _colors[k];
        }
        Array.Copy(tmp, 0, _colors, start, end - start);
    }

    private static void Prepare(IndexedImage dst, int w, int h)
    {
        dst.Width = w;
        dst.Height = h;
        if (dst.Indices.Length < w * h) dst.Indices = new byte[w * h];
        if (dst.Palette.Length < 256) dst.Palette = new int[256];
    }
}
