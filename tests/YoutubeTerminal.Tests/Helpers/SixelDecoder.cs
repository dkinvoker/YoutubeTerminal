using System.Text;

namespace YoutubeTerminal.Tests.Helpers;

/// <summary>Prosty dekoder Sixel (DCS ... q ... ST) do sprawdzania wyjścia enkodera.</summary>
internal static class SixelDecoder
{
    public sealed record Result(uint[] Pixels, int Width, int Height, int Colors);

    public static Result Decode(byte[] data)
    {
        int i = Array.IndexOf(data, (byte)'q');
        if (i < 0) throw new InvalidDataException("Brak początku danych Sixel");
        var pal = new Dictionary<int, uint>();
        int w = 0, h = 0, x = 0, y = 0, color = 0;
        uint[] px = [];
        int p = i + 1;

        int Num()
        {
            int v = 0;
            while (p < data.Length && data[p] >= '0' && data[p] <= '9') v = v * 10 + (data[p++] - '0');
            return v;
        }

        while (p < data.Length)
        {
            byte c = data[p];
            if (c == 0x1B) break;
            if (c == '"')
            {
                p++; Num(); p++; Num(); p++;
                w = Num(); p++; h = Num();
                px = new uint[w * h];
                continue;
            }
            if (c == '#')
            {
                p++;
                int reg = Num();
                if (p < data.Length && data[p] == ';')
                {
                    p++; Num(); p++;
                    int r = Num(); p++;
                    int g = Num(); p++;
                    int b = Num();
                    pal[reg] = 0xFF000000u | ((uint)(r * 255 / 100) << 16) | ((uint)(g * 255 / 100) << 8) | (uint)(b * 255 / 100);
                }
                color = reg;
                continue;
            }
            if (c == '$') { x = 0; p++; continue; }
            if (c == '-') { x = 0; y += 6; p++; continue; }
            int rep = 1;
            if (c == '!') { p++; rep = Num(); c = data[p]; }
            if (c >= 63 && c <= 126)
            {
                int bits = c - 63;
                for (int k = 0; k < rep; k++, x++)
                    for (int b = 0; b < 6; b++)
                        if ((bits & (1 << b)) != 0 && x < w && y + b < h)
                            px[(y + b) * w + x] = pal.TryGetValue(color, out var col) ? col : 0;
            }
            p++;
        }
        return new Result(px, w, h, pal.Count);
    }

    /// <summary>Wyciąga z wyjścia kolejne sekwencje CUP (ESC[y;xH) jako pozycje 0-based.</summary>
    public static List<(int X, int Y)> CursorMoves(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);
        var list = new List<(int, int)>();
        int idx = 0;
        while ((idx = s.IndexOf("\x1b[", idx, StringComparison.Ordinal)) >= 0)
        {
            // parametry (cyfry i ';') aż do znaku końcowego sekwencji CSI
            int end = idx + 2;
            while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == ';')) end++;
            if (end >= s.Length) break;
            if (s[end] == 'H')
            {
                var parts = s[(idx + 2)..end].Split(';');
                if (parts.Length == 2 && int.TryParse(parts[0], out int row) && int.TryParse(parts[1], out int col))
                    list.Add((col - 1, row - 1));
            }
            idx = end;
        }
        return list;
    }
}
