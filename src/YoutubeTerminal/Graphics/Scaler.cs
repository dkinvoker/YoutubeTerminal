namespace YoutubeTerminal.Graphics;

/// <summary>
/// Szybki separowalny resampler (uśrednianie powierzchniowe przy zmniejszaniu,
/// interpolacja dwuliniowa przy powiększaniu) na arytmetyce stałoprzecinkowej.
/// </summary>
internal static unsafe class Scaler
{
    private const int Shift = 14;
    private const int One = 1 << Shift;

    private sealed class Taps
    {
        public int[] Start = [];
        public int[] Count = [];
        public int[] Weights = []; // spłaszczone: dla każdego wyjścia Count wag
        public int[] Offset = [];
    }

    // Prosty cache tablic wag (rozmiary klatek wideo powtarzają się).
    [ThreadStatic] private static Dictionary<long, Taps>? _tapCache;
    [ThreadStatic] private static int[]? _tmp;

    private static Taps GetTaps(int src, int dst)
    {
        _tapCache ??= new Dictionary<long, Taps>();
        long key = ((long)src << 32) | (uint)dst;
        if (_tapCache.TryGetValue(key, out var t)) return t;
        if (_tapCache.Count > 32) _tapCache.Clear();
        t = BuildTaps(src, dst);
        _tapCache[key] = t;
        return t;
    }

    private static Taps BuildTaps(int src, int dst)
    {
        var starts = new int[dst];
        var counts = new int[dst];
        var offsets = new int[dst];
        var weights = new List<int>(dst * 4);
        double scale = (double)src / dst;

        for (int i = 0; i < dst; i++)
        {
            var w = new List<(int idx, double w)>(8);
            if (scale > 1.0)
            {
                // Zmniejszanie: pokrycie powierzchni piksela źródłowego.
                double a = i * scale, b = (i + 1) * scale;
                int s = (int)Math.Floor(a), e = Math.Min(src - 1, (int)Math.Ceiling(b) - 1);
                for (int k = s; k <= e; k++)
                {
                    double cover = Math.Min(b, k + 1) - Math.Max(a, k);
                    if (cover > 1e-6) w.Add((k, cover));
                }
                if (w.Count == 0) w.Add((Math.Clamp(s, 0, src - 1), 1));
            }
            else
            {
                // Powiększanie: dwuliniowa.
                double c = (i + 0.5) * scale - 0.5;
                int k0 = (int)Math.Floor(c);
                double f = c - k0;
                int a0 = Math.Clamp(k0, 0, src - 1), a1 = Math.Clamp(k0 + 1, 0, src - 1);
                if (a0 == a1) w.Add((a0, 1));
                else { w.Add((a0, 1 - f)); w.Add((a1, f)); }
            }

            double sum = 0;
            foreach (var p in w) sum += p.w;
            starts[i] = w[0].idx;
            counts[i] = w[^1].idx - w[0].idx + 1;
            offsets[i] = weights.Count;
            var arr = new int[counts[i]];
            int acc = 0;
            foreach (var p in w)
            {
                int q = (int)Math.Round(p.w / sum * One);
                arr[p.idx - starts[i]] += q;
                acc += q;
            }
            // Korekta zaokrągleń, żeby suma była dokładnie One.
            arr[arr.Length / 2] += One - acc;
            weights.AddRange(arr);
        }

        return new Taps { Start = starts, Count = counts, Offset = offsets, Weights = weights.ToArray() };
    }

    public static Bitmap Resize(Bitmap src, int dw, int dh)
    {
        if (dw == src.Width && dh == src.Height)
            return new Bitmap(dw, dh, (uint[])src.Pixels.Clone());
        var dst = new Bitmap(dw, dh);
        fixed (uint* s = src.Pixels)
            Resize(s, src.Width, src.Height, src.Width, dst.Pixels, dw, dh);
        return dst;
    }

    /// <summary>
    /// Skaluje obraz źródłowy (wskaźnik, szerokość, wysokość, stride w pikselach) do bufora docelowego.
    /// </summary>
    public static void Resize(uint* src, int sw, int sh, int sstride, uint[] dst, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            fixed (uint* d = dst)
                for (int y = 0; y < dh; y++)
                    Buffer.MemoryCopy(src + (long)y * sstride, d + (long)y * dw, (long)dw * 4, (long)dw * 4);
            return;
        }

        var tx = GetTaps(sw, dw);
        var ty = GetTaps(sh, dh);

        // Przebieg poziomy: sh wierszy x dw kolumn x 3 kanały
        int tmpLen = sh * dw * 3;
        if (_tmp == null || _tmp.Length < tmpLen) _tmp = new int[tmpLen];
        var tmp = _tmp;

        int threads = (long)dw * dh >= 120_000 || (long)sw * sh >= 300_000 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : 1;
        if (threads > 1)
        {
            uint* srcP = src;
            // Przebieg poziomy równolegle po wierszach źródła, potem pionowy po wierszach celu.
            Parallel.For(0, threads, t => HorizontalPass(srcP, sstride, tx, tmp, dw, sh * t / threads, sh * (t + 1) / threads));
            Parallel.For(0, threads, t => VerticalPass(ty, tmp, dst, dw, dh * t / threads, dh * (t + 1) / threads));
            return;
        }
        HorizontalPass(src, sstride, tx, tmp, dw, 0, sh);
        VerticalPass(ty, tmp, dst, dw, 0, dh);
    }

    private static void HorizontalPass(uint* src, int sstride, Taps tx, int[] tmp, int dw, int yStart, int yEnd)
    {
        fixed (int* tw = tx.Weights)
        fixed (int* tmpP = tmp)
        {
            for (int y = yStart; y < yEnd; y++)
            {
                uint* row = src + (long)y * sstride;
                int* outRow = tmpP + y * dw * 3;
                for (int x = 0; x < dw; x++)
                {
                    int st = tx.Start[x], cnt = tx.Count[x];
                    int* wp = tw + tx.Offset[x];
                    int r = 0, g = 0, b = 0;
                    uint* p = row + st;
                    for (int k = 0; k < cnt; k++)
                    {
                        uint c = p[k];
                        int w = wp[k];
                        r += (int)((c >> 16) & 0xFF) * w;
                        g += (int)((c >> 8) & 0xFF) * w;
                        b += (int)(c & 0xFF) * w;
                    }
                    outRow[x * 3] = r >> 6;      // zachowujemy 8 bitów precyzji ułamkowej
                    outRow[x * 3 + 1] = g >> 6;
                    outRow[x * 3 + 2] = b >> 6;
                }
            }
        }
    }

    private static void VerticalPass(Taps ty, int[] tmp, uint[] dst, int dw, int yStart, int yEnd)
    {
        fixed (int* tmpP = tmp)
        {
            fixed (int* twy = ty.Weights)
            fixed (uint* d = dst)
            {
                for (int y = yStart; y < yEnd; y++)
                {
                    int st = ty.Start[y], cnt = ty.Count[y];
                    int* wp = twy + ty.Offset[y];
                    uint* drow = d + y * dw;
                    for (int x = 0; x < dw; x++)
                    {
                        long r = 0, g = 0, b = 0;
                        int* p = tmpP + st * dw * 3 + x * 3;
                        for (int k = 0; k < cnt; k++)
                        {
                            int w = wp[k];
                            r += (long)p[0] * w;
                            g += (long)p[1] * w;
                            b += (long)p[2] * w;
                            p += dw * 3;
                        }
                        // skala: 2^14 (pozioma) / 2^6 * 2^14 (pionowa) = 2^22
                        int R = (int)((r + (1 << 21)) >> 22);
                        int G = (int)((g + (1 << 21)) >> 22);
                        int B = (int)((b + (1 << 21)) >> 22);
                        if (R > 255) R = 255; else if (R < 0) R = 0;
                        if (G > 255) G = 255; else if (G < 0) G = 0;
                        if (B > 255) B = 255; else if (B < 0) B = 0;
                        drow[x] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                    }
                }
            }
        }
    }
}
