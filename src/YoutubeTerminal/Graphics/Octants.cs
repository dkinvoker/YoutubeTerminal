using System.Text;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Graphics;

/// <summary>
/// Oktanty (Unicode 16, U+1CD00–U+1CDE5): komórka to siatka 2×4 „piksele”, każdy w kolorze tekstu albo tła.
/// Numeracja pikseli jak w nazwach Unicode (BLOCK OCTANT-1234…): 1 2 / 3 4 / 5 6 / 7 8, bit i−1 = piksel i.
/// Blok zawiera 230 wzorów w kolejności wartości bitowej; pozostałe 26 to znaki z innych bloków
/// (spacja, półbloki, ćwiartki…). Kilku z nich (np. pojedynczy piksel narożny) brakuje w starszych
/// czcionkach — takie wzory rysujemy jako dopełnienie z zamienionymi kolorami.
/// </summary>
internal static class Octants
{
    public const int CellPixelsX = 2, CellPixelsY = 4;

    /// <summary>UTF-8 znaku dla wzoru albo null, gdy wzór trzeba narysować jako dopełnienie.</summary>
    public static readonly byte[]?[] Glyphs = BuildGlyphs();

    /// <summary>Liczba znaków użytych z bloku oktantów (kontrola tabeli: musi wynosić 230).</summary>
    public static int BlockGlyphCount { get; private set; }

    private static byte[]?[] BuildGlyphs()
    {
        // Wzory spoza bloku oktantów z powszechnie obsługiwanymi znakami (Block Elements, U+2580–U+259F).
        var common = new Dictionary<int, int>
        {
            [0x00] = ' ', [0xFF] = 0x2588,
            [0x0F] = 0x2580, [0xF0] = 0x2584, [0x55] = 0x258C, [0xAA] = 0x2590,
            [0x05] = 0x2598, [0x0A] = 0x259D, [0x50] = 0x2596, [0xA0] = 0x2597,
            [0xF5] = 0x2599, [0xA5] = 0x259A, [0x5F] = 0x259B, [0xAF] = 0x259C, [0x5A] = 0x259E, [0xFA] = 0x259F,
            [0xC0] = 0x2582, [0xFC] = 0x2586,
        };
        // Wzory spoza bloku ze znakami z nowszych/rzadszych bloków (U+1FBxx, U+1CEAx) — rysowane jako dopełnienie.
        int[] rare = [0x01, 0x02, 0x40, 0x80, 0x14, 0x28, 0x03, 0x3F];

        var glyphs = new byte[]?[256];
        int next = 0x1CD00;
        for (int p = 0; p < 256; p++)
        {
            if (common.TryGetValue(p, out int cp)) glyphs[p] = Utf8(cp);
            else if (Array.IndexOf(rare, p) >= 0) glyphs[p] = null;
            else glyphs[p] = Utf8(next++);
        }
        BlockGlyphCount = next - 0x1CD00;
        return glyphs;
    }

    private static byte[] Utf8(int codePoint) => Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint));

    /// <summary>
    /// Wybiera dla 8 pikseli komórki dwa kolory i wzór (2-średnie: podział po kanale o największym
    /// rozrzucie, potem jedno przypisanie do bliższej średniej). Zwraca wzór; kolor „1” to piksele z bitem.
    /// </summary>
    public static int Fit(ReadOnlySpan<uint> px, out uint color1, out uint color0)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        for (int i = 0; i < 8; i++)
        {
            uint c = px[i];
            int r = (int)((c >> 16) & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)(c & 0xFF);
            if (r < rMin) rMin = r; if (r > rMax) rMax = r;
            if (g < gMin) gMin = g; if (g > gMax) gMax = g;
            if (b < bMin) bMin = b; if (b > bMax) bMax = b;
        }
        int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
        int range = Math.Max(rr, Math.Max(gr, br));
        if (range < 10)
        {
            color1 = color0 = Mean(px, 0xFF);
            return 0;
        }
        int shift = rr >= gr && rr >= br ? 16 : gr >= br ? 8 : 0;
        int mid = shift == 16 ? (rMin + rMax) / 2 : shift == 8 ? (gMin + gMax) / 2 : (bMin + bMax) / 2;
        int mask = 0;
        for (int i = 0; i < 8; i++)
            if ((int)((px[i] >> shift) & 0xFF) > mid) mask |= 1 << i;

        color1 = Mean(px, mask);
        color0 = Mean(px, ~mask & 0xFF);
        // Jedna iteracja k-średnich: każdy piksel do bliższego z dwóch kolorów.
        int refined = 0;
        for (int i = 0; i < 8; i++)
            if (Dist(px[i], color1) < Dist(px[i], color0)) refined |= 1 << i;
        if (refined != mask && refined != 0 && refined != 0xFF)
        {
            mask = refined;
            color1 = Mean(px, mask);
            color0 = Mean(px, ~mask & 0xFF);
        }
        return mask;
    }

    private static uint Mean(ReadOnlySpan<uint> px, int mask)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            uint c = px[i];
            r += (int)((c >> 16) & 0xFF); g += (int)((c >> 8) & 0xFF); b += (int)(c & 0xFF);
            n++;
        }
        if (n == 0) return 0;
        return (uint)(((r + n / 2) / n) << 16 | ((g + n / 2) / n) << 8 | ((b + n / 2) / n));
    }

    private static int Dist(uint a, uint b)
    {
        int dr = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        int dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int db = (int)(a & 0xFF) - (int)(b & 0xFF);
        return dr * dr * 3 + dg * dg * 4 + db * db * 2;
    }

    /// <summary>
    /// Koduje wiersze komórek (po 4 wiersze pikseli) do znaków oktantów z kolorami 24-bit.
    /// <paramref name="y0"/> to pierwszy wiersz pikseli, <paramref name="sh"/> liczba wierszy (wielokrotność 4).
    /// </summary>
    public static void EncodeRows(ByteBuilder o, uint[] px, int w, int y0, int sh, int cellX, int cellY)
    {
        Span<uint> cell = stackalloc uint[8];
        uint lastFg = 0xFFFFFFFF, lastBg = 0xFFFFFFFF;
        int rows = sh / CellPixelsY, cols = w / CellPixelsX;
        for (int r = 0; r < rows; r++)
        {
            o.MoveTo(cellX, cellY + r);
            int rowBase = (y0 + r * CellPixelsY) * w;
            for (int cx = 0; cx < cols; cx++)
            {
                int x = cx * CellPixelsX;
                for (int k = 0; k < 4; k++)
                {
                    int off = rowBase + k * w + x;
                    cell[k * 2] = px[off];
                    cell[k * 2 + 1] = px[off + 1];
                }
                int mask = Fit(cell, out uint c1, out uint c0);
                c1 &= 0xFCFCFC; c0 &= 0xFCFCFC;
                if (mask == 0 || mask == 0xFF || c1 == c0)
                {
                    uint solid = mask == 0xFF ? c1 : c0;
                    if (solid != lastBg) { Sgr(o, null, solid); lastBg = solid; }
                    o.Append((byte)' ');
                    continue;
                }
                int inv = ~mask & 0xFF;
                var direct = Glyphs[mask];
                var inverse = Glyphs[inv];
                bool useInverse;
                if (direct == null) useInverse = true;
                else if (inverse == null) useInverse = false;
                else
                {
                    // Wybieramy wariant wymagający mniej zmian kolorów względem poprzedniej komórki.
                    int costDirect = (c1 != lastFg ? 1 : 0) + (c0 != lastBg ? 1 : 0);
                    int costInverse = (c0 != lastFg ? 1 : 0) + (c1 != lastBg ? 1 : 0);
                    useInverse = costInverse < costDirect;
                }
                uint fg = useInverse ? c0 : c1, bg = useInverse ? c1 : c0;
                var glyph = useInverse ? inverse! : direct!;
                bool fgChange = fg != lastFg, bgChange = bg != lastBg;
                if (fgChange || bgChange) Sgr(o, fgChange ? fg : null, bgChange ? bg : null);
                lastFg = fg; lastBg = bg;
                o.Append(glyph);
            }
        }
        o.AppendAscii("\x1b[0m");
    }

    /// <summary>Jedna sekwencja SGR dla koloru tekstu i/lub tła (krócej niż dwie osobne).</summary>
    private static void Sgr(ByteBuilder o, uint? fg, uint? bg)
    {
        o.AppendAscii("\x1b[");
        if (fg is uint f)
        {
            o.AppendAscii("38;2;");
            o.AppendInt((int)((f >> 16) & 0xFF)); o.Append((byte)';');
            o.AppendInt((int)((f >> 8) & 0xFF)); o.Append((byte)';');
            o.AppendInt((int)(f & 0xFF));
            if (bg != null) o.Append((byte)';');
        }
        if (bg is uint b)
        {
            o.AppendAscii("48;2;");
            o.AppendInt((int)((b >> 16) & 0xFF)); o.Append((byte)';');
            o.AppendInt((int)((b >> 8) & 0xFF)); o.Append((byte)';');
            o.AppendInt((int)(b & 0xFF));
        }
        o.Append((byte)'m');
    }
}
