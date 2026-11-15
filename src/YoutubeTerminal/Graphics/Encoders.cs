using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Graphics;

/// <summary>Sposób rysowania obrazów: Sixel (piksele), oktanty (2×4 na komórkę) lub półbloki (1×2 na komórkę).</summary>
internal enum RenderMode { Sixel, Octant, HalfBlock }

/// <summary>Rozmiar obrazu w pikselach i jego położenie w komórkach.</summary>
internal readonly record struct ImageLayout(int PixelWidth, int PixelHeight, Rect Cells);

/// <summary>
/// Klatka wideo podzielona na poziome paski. Każdy pasek to samodzielna sekwencja (z pozycjonowaniem),
/// a sygnatura pozwala pominąć wysyłanie pasków, które na ekranie wyglądają tak samo.
/// </summary>
internal sealed class EncodedFrame
{
    public TimeSpan Time;
    public byte[][] Strips = [];
    public byte[][] Signatures = [];

    public int TotalBytes => Strips.Sum(s => s.Length);
}

/// <summary>Kodowanie bitmap do sekwencji terminala (Sixel albo półbloki ▀ z kolorami 24-bit).</summary>
internal sealed class ImageEncoder
{
    private readonly Quantizer _quantizer = new();
    private readonly IndexedImage _indexed = new();
    private readonly ByteBuilder _out = new(1 << 18);
    private BandState[] _states = [];

    /// <summary>Stan kodowania fragmentu pasm (osobny dla każdego wątku).</summary>
    private sealed class BandState
    {
        public byte[] Bits = [];
        public readonly int[] XMin = new int[256];
        public readonly int[] XMax = new int[256];
        public readonly bool[] Used = new bool[256];
        public readonly int[] UsedList = new int[256];
        public readonly ByteBuilder Out = new(1 << 16);
        public byte[] Seg = [];
    }

    public RenderMode Mode;
    public int CellWidth = 10, CellHeight = 20;
    public bool AdaptivePalette = true;
    public bool Dither;
    /// <summary>Maksymalna liczba kolorów palety adaptacyjnej (mniej = mniejsze klatki).</summary>
    public int MaxColors = 255;

    /// <summary>
    /// Wylicza rozmiar w pikselach dla obrazu o proporcjach srcW:srcH, który ma się zmieścić w obszarze komórek,
    /// oraz wyśrodkowany prostokąt komórek, które faktycznie zajmie.
    /// </summary>
    public ImageLayout Layout(int srcW, int srcH, Rect area, double scale = 1.0)
    {
        if (area.W <= 0 || area.H <= 0) return new ImageLayout(0, 0, area);
        if (Mode == RenderMode.Sixel)
        {
            int maxW = (int)(area.W * CellWidth * scale);
            int maxH = (int)(area.H * CellHeight * scale);
            var (w, h) = Bitmap.FitSize(srcW, srcH, maxW, maxH);
            h = Math.Max(6, h / 6 * 6);
            if (h > area.H * CellHeight) h = area.H * CellHeight / 6 * 6;
            int cols = Math.Min(area.W, (w + CellWidth - 1) / CellWidth);
            int rows = Math.Min(area.H, (h + CellHeight - 1) / CellHeight);
            int x = area.X + (area.W - cols) / 2;
            int y = area.Y + (area.H - rows) / 2;
            return new ImageLayout(w, h, new Rect(x, y, cols, rows));
        }
        else
        {
            // Znaki blokowe: komórka = px×py pikseli; proporcja piksela = (CellWidth/px) / (CellHeight/py).
            var (cpx, cpy) = CellPixels(Mode);
            double pixelAspect = (CellWidth / (double)cpx) / (CellHeight / (double)cpy);
            int maxW = (int)(area.W * cpx * scale);
            int maxH = (int)(area.H * cpy * scale);
            double srcAspect = (double)srcW / Math.Max(1, srcH) / pixelAspect;
            int w = maxW, h = (int)Math.Round(w / srcAspect);
            if (h > maxH) { h = maxH; w = (int)Math.Round(h * srcAspect); }
            w = Math.Clamp(w / cpx * cpx, cpx, area.W * cpx);
            h = Math.Clamp(h / cpy * cpy, cpy, area.H * cpy);
            int cols = w / cpx, rows = h / cpy;
            int x = area.X + (area.W - cols) / 2;
            int y = area.Y + (area.H - rows) / 2;
            return new ImageLayout(w, h, new Rect(x, y, cols, rows));
        }
    }

    /// <summary>Pikseli obrazu na komórkę w trybach znakowych (Sixel: rozmiar liczony z komórki w px).</summary>
    public static (int X, int Y) CellPixels(RenderMode mode) => mode switch
    {
        RenderMode.Octant => (Octants.CellPixelsX, Octants.CellPixelsY),
        RenderMode.HalfBlock => (1, 2),
        _ => (1, 1),
    };

    /// <summary>Rozmiar w pikselach obrazu wypełniającego cały obszar komórek.</summary>
    public (int W, int H) PixelSize(Rect area)
    {
        if (Mode == RenderMode.Sixel) return (area.W * CellWidth, area.H * CellHeight / 6 * 6);
        var (cpx, cpy) = CellPixels(Mode);
        return (area.W * cpx, area.H * cpy);
    }

    /// <summary>Koduje bitmapę (o rozmiarze z <see cref="Layout"/>) do bajtów gotowych do zapisu (z pozycjonowaniem kursora).</summary>
    public byte[] Encode(uint[] px, int w, int h, Rect cells, bool forVideo = false)
    {
        _out.Clear();
        if (Mode == RenderMode.Sixel) EncodeSixel(px, w, h, cells, forVideo);
        else if (Mode == RenderMode.Octant) Octants.EncodeRows(_out, px, w, 0, h, cells.X, cells.Y);
        else EncodeHalfBlockRows(_out, px, w, 0, h, cells.X, cells.Y);
        return _out.ToArray();
    }

    public byte[] Encode(Bitmap bmp, Rect cells, bool forVideo = false) => Encode(bmp.Pixels, bmp.Width, bmp.Height, cells, forVideo);

    /// <summary>Liczba wierszy komórek w pasku: wysokość paska musi być wielokrotnością 6 px (pasmo Sixel) i wysokości komórki.</summary>
    private int StripRows
    {
        get
        {
            if (Mode != RenderMode.Sixel) return 2;
            int g = Gcd(CellHeight, 6);
            int rows = 6 / g;
            while (rows * CellHeight < 48) rows += 6 / g; // paski nie za niskie (narzut nagłówków)
            return rows;
        }
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>Koduje klatkę wideo jako zestaw pasków (patrz <see cref="EncodedFrame"/>).</summary>
    public EncodedFrame EncodeVideoFrame(uint[] px, int w, int h, Rect cells)
    {
        int stripRows = StripRows;
        int stripPx = Mode == RenderMode.Sixel ? stripRows * CellHeight : stripRows * CellPixels(Mode).Y;
        int count = (h + stripPx - 1) / stripPx;
        var frame = new EncodedFrame { Strips = new byte[count][], Signatures = new byte[count][] };

        if (Mode == RenderMode.Sixel)
        {
            if (AdaptivePalette && !Dither) _quantizer.QuantizeAdaptive(px, w, h, _indexed, Math.Clamp(MaxColors, 2, 255));
            else _quantizer.QuantizeFixed(px, w, h, _indexed, Dither);
        }

        if (_states.Length < count)
        {
            var ns = new BandState[count];
            for (int i = 0; i < count; i++) ns[i] = i < _states.Length ? _states[i] : new BandState();
            _states = ns;
        }
        var states = _states;
        var idx = _indexed;
        Parallel.For(0, count, s =>
        {
            int y0 = s * stripPx;
            int sh = Math.Min(stripPx, h - y0);
            var st = states[s];
            st.Out.Clear();
            int cy = cells.Y + s * stripRows;
            if (Mode == RenderMode.Sixel)
                EncodeSixelStrip(st, idx, w, y0, sh, cells.X, cy);
            else if (Mode == RenderMode.Octant)
                Octants.EncodeRows(st.Out, px, w, y0, sh, cells.X, cy);
            else
                EncodeHalfBlockRows(st.Out, px, w, y0, sh, cells.X, cy);
            frame.Strips[s] = st.Out.ToArray();
            frame.Signatures[s] = Signature(px, w, y0, sh);
        });
        return frame;
    }

    private static void EncodeSixelStrip(BandState st, IndexedImage img, int w, int y0, int sh, int cellX, int cellY)
    {
        var o = st.Out;
        o.MoveTo(cellX, cellY);
        o.AppendAscii("\x1bP0;1;0q\"1;1;");
        o.AppendInt(w); o.Append((byte)';'); o.AppendInt(sh);
        var idx = img.Indices;
        Span<bool> used = stackalloc bool[256];
        for (int i = y0 * w, end = (y0 + sh) * w; i < end; i++) used[idx[i]] = true;
        var pal = img.Palette;
        for (int c = 0; c < img.PaletteSize; c++)
        {
            if (!used[c]) continue;
            int rgb = pal[c];
            o.Append((byte)'#'); o.AppendInt(c); o.AppendAscii(";2;");
            o.AppendInt((((rgb >> 16) & 0xFF) * 100 + 127) / 255); o.Append((byte)';');
            o.AppendInt((((rgb >> 8) & 0xFF) * 100 + 127) / 255); o.Append((byte)';');
            o.AppendInt(((rgb & 0xFF) * 100 + 127) / 255);
        }
        // Pasma tego paska: indeksy liczone względem początku obrazu, ale z ograniczeniem do paska.
        var sub = new ArraySegment<byte>(idx, y0 * w, sh * w);
        EncodeBandsSegment(st, sub, w, sh);
        o.AppendAscii("\x1b\\");
    }

    private static void EncodeBandsSegment(BandState st, ArraySegment<byte> seg, int w, int h)
    {
        var tmp = seg.Array!;
        int off = seg.Offset;
        if (off == 0)
        {
            EncodeBands(st, tmp, w, h, 0, (h + 5) / 6);
            return;
        }
        // Kopia do bufora roboczego (EncodeBands indeksuje od zera).
        if (st.Seg.Length < seg.Count) st.Seg = new byte[seg.Count];
        Array.Copy(tmp, off, st.Seg, 0, seg.Count);
        EncodeBands(st, st.Seg, w, h, 0, (h + 5) / 6);
    }

    /// <summary>Zgrubna sygnatura paska: średnie kolory bloków 8×8 (do wykrywania zmian).</summary>
    private static byte[] Signature(uint[] px, int w, int y0, int sh)
    {
        const int B = 8;
        int bw = (w + B - 1) / B, bh = (sh + B - 1) / B;
        var sig = new byte[bw * bh * 3];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                int yEnd = Math.Min(sh, (by + 1) * B), xEnd = Math.Min(w, (bx + 1) * B);
                for (int y = by * B; y < yEnd; y += 2)
                {
                    int row = (y0 + y) * w;
                    for (int x = bx * B; x < xEnd; x += 2)
                    {
                        uint c = px[row + x];
                        r += (int)((c >> 16) & 0xFF); g += (int)((c >> 8) & 0xFF); b += (int)(c & 0xFF);
                        n++;
                    }
                }
                int i = (by * bw + bx) * 3;
                n = Math.Max(1, n);
                sig[i] = (byte)(r / n); sig[i + 1] = (byte)(g / n); sig[i + 2] = (byte)(b / n);
            }
        }
        return sig;
    }

    /// <summary>Czy dwa paski wyglądają tak samo (z tolerancją na szum kompresji)?</summary>
    public static bool SimilarSignature(byte[]? a, byte[] b, int tolerance = 5)
    {
        if (a == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > tolerance) return false;
        return true;
    }

    private static void EncodeHalfBlockRows(ByteBuilder o, uint[] px, int w, int y0, int sh, int cellX, int cellY)
    {
        uint lastFg = 0xFFFFFFFF, lastBg = 0xFFFFFFFF;
        int rows = sh / 2;
        for (int r = 0; r < rows; r++)
        {
            o.MoveTo(cellX, cellY + r);
            int top = (y0 + r * 2) * w, bot = (y0 + r * 2 + 1) * w;
            for (int x = 0; x < w; x++)
            {
                uint t = px[top + x] & 0xFCFCFC;
                uint b = px[bot + x] & 0xFCFCFC;
                if (t == b)
                {
                    if (b != lastBg) { Bg(o, b); lastBg = b; }
                    o.Append((byte)' ');
                }
                else
                {
                    if (t != lastFg) { Fg(o, t); lastFg = t; }
                    if (b != lastBg) { Bg(o, b); lastBg = b; }
                    o.Append(0xE2); o.Append(0x96); o.Append(0x80);
                }
            }
        }
        o.AppendAscii("\x1b[0m");
    }

    private void EncodeSixel(uint[] px, int w, int h, Rect cells, bool forVideo)
    {
        if (AdaptivePalette && !Dither) _quantizer.QuantizeAdaptive(px, w, h, _indexed, Math.Clamp(MaxColors, 2, 255));
        else _quantizer.QuantizeFixed(px, w, h, _indexed, Dither);

        var o = _out;
        o.MoveTo(cells.X, cells.Y);
        // DCS P1=0;P2=1 (nieustawione piksele przezroczyste);P3=0 q, atrybuty rastra 1:1
        o.AppendAscii("\x1bP0;1;0q\"1;1;");
        o.AppendInt(w); o.Append((byte)';'); o.AppendInt(h);

        var pal = _indexed.Palette;
        int palSize = _indexed.PaletteSize;
        var idx = _indexed.Indices;

        // Definicje kolorów (tylko użyte)
        Span<bool> anyUsed = stackalloc bool[256];
        for (int i = 0; i < w * h; i++) anyUsed[idx[i]] = true;
        for (int c = 0; c < palSize; c++)
        {
            if (!anyUsed[c]) continue;
            int rgb = pal[c];
            o.Append((byte)'#'); o.AppendInt(c); o.AppendAscii(";2;");
            o.AppendInt((((rgb >> 16) & 0xFF) * 100 + 127) / 255); o.Append((byte)';');
            o.AppendInt((((rgb >> 8) & 0xFF) * 100 + 127) / 255); o.Append((byte)';');
            o.AppendInt(((rgb & 0xFF) * 100 + 127) / 255);
        }

        // Pasma (po 6 wierszy) są niezależne — dzielimy je między wątki.
        int bands = (h + 5) / 6;
        int chunks = w * h >= 60000 ? Math.Clamp(Math.Min(Environment.ProcessorCount, bands / 3), 1, 16) : 1;
        if (_states.Length < chunks)
        {
            var ns = new BandState[chunks];
            for (int i = 0; i < chunks; i++) ns[i] = i < _states.Length ? _states[i] : new BandState();
            _states = ns;
        }
        if (chunks == 1)
        {
            var st = _states[0];
            st.Out.Clear();
            EncodeBands(st, idx, w, h, 0, bands);
            o.Append(st.Out.Span);
        }
        else
        {
            var states = _states;
            Parallel.For(0, chunks, c =>
            {
                int b0 = bands * c / chunks, b1 = bands * (c + 1) / chunks;
                var st = states[c];
                st.Out.Clear();
                EncodeBands(st, idx, w, h, b0, b1);
            });
            for (int c = 0; c < chunks; c++) o.Append(states[c].Out.Span);
        }
        o.AppendAscii("\x1b\\");
        _ = forVideo;
    }

    private static void EncodeBands(BandState st, byte[] idx, int w, int h, int bandStart, int bandEnd)
    {
        if (st.Bits.Length < 256 * w) st.Bits = new byte[256 * w];
        var bits = st.Bits;
        var used = st.Used;
        var usedList = st.UsedList;
        var xmin = st.XMin;
        var xmax = st.XMax;
        var o = st.Out;
        int lastBand = (h + 5) / 6 - 1;

        for (int band = bandStart; band < bandEnd; band++)
        {
            int y0 = band * 6;
            int usedCount = 0;
            int rows = Math.Min(6, h - y0);
            for (int r = 0; r < rows; r++)
            {
                int rowOff = (y0 + r) * w;
                byte bit = (byte)(1 << r);
                for (int x = 0; x < w; x++)
                {
                    int c = idx[rowOff + x];
                    if (!used[c])
                    {
                        used[c] = true;
                        usedList[usedCount++] = c;
                        xmin[c] = x;
                        xmax[c] = x;
                    }
                    else
                    {
                        if (x < xmin[c]) xmin[c] = x;
                        if (x > xmax[c]) xmax[c] = x;
                    }
                    bits[c * w + x] |= bit;
                }
            }

            for (int u = 0; u < usedCount; u++)
            {
                int c = usedList[u];
                int baseOff = c * w;
                o.Append((byte)'#'); o.AppendInt(c);
                int x0 = xmin[c], x1 = xmax[c];
                if (x0 > 0) EmitRun(o, 0, x0); // puste piksele (znak '?')

                int x = x0;
                while (x <= x1)
                {
                    byte v = bits[baseOff + x];
                    int run = 1;
                    while (x + run <= x1 && bits[baseOff + x + run] == v) run++;
                    EmitRun(o, v, run);
                    x += run;
                }
                // czyszczenie użytego zakresu
                Array.Clear(bits, baseOff + x0, x1 - x0 + 1);
                used[c] = false;
                if (u < usedCount - 1) o.Append((byte)'$');
            }
            // Bez '-' po ostatnim paśmie (inaczej terminal może przewinąć ekran o jedną linię).
            if (band < lastBand) o.Append((byte)'-');
        }
    }

    private static void EmitRun(ByteBuilder o, int v, int run)
    {
        byte ch = (byte)(63 + v);
        if (run > 3)
        {
            o.Append((byte)'!');
            o.AppendInt(run);
            o.Append(ch);
        }
        else
        {
            for (int i = 0; i < run; i++) o.Append(ch);
        }
    }

    private static void Fg(ByteBuilder o, uint c)
    {
        o.AppendAscii("\x1b[38;2;");
        o.AppendInt((int)((c >> 16) & 0xFF)); o.Append((byte)';');
        o.AppendInt((int)((c >> 8) & 0xFF)); o.Append((byte)';');
        o.AppendInt((int)(c & 0xFF)); o.Append((byte)'m');
    }

    private static void Bg(ByteBuilder o, uint c)
    {
        o.AppendAscii("\x1b[48;2;");
        o.AppendInt((int)((c >> 16) & 0xFF)); o.Append((byte)';');
        o.AppendInt((int)((c >> 8) & 0xFF)); o.Append((byte)';');
        o.AppendInt((int)(c & 0xFF)); o.Append((byte)'m');
    }
}
