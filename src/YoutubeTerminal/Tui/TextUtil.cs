using System.Globalization;
using System.Text;

namespace YoutubeTerminal.Tui;

/// <summary>Szerokości znaków w komórkach terminala, podział na grafemy, zawijanie i przycinanie.</summary>
internal static class TextUtil
{
    private static readonly string[] AsciiStrings = Enumerable.Range(0, 128).Select(i => ((char)i).ToString()).ToArray();

    public static int RuneWidth(Rune r)
    {
        int v = r.Value;
        if (v < 0x20 || (v >= 0x7F && v < 0xA0)) return 0;
        if (v < 0x300) return 1;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        if (v == 0x200B) return 0;
        if (IsWide(v)) return 2;
        return 1;
    }

    private static bool IsWide(int v) =>
        (v >= 0x1100 && v <= 0x115F) ||
        (v >= 0x231A && v <= 0x231B) || (v >= 0x23E9 && v <= 0x23EC) || v == 0x23F0 || v == 0x23F3 ||
        (v >= 0x25FD && v <= 0x25FE) || (v >= 0x2614 && v <= 0x2615) || (v >= 0x2648 && v <= 0x2653) ||
        v == 0x267F || v == 0x2693 || v == 0x26A1 || (v >= 0x26AA && v <= 0x26AB) || (v >= 0x26BD && v <= 0x26BE) ||
        (v >= 0x26C4 && v <= 0x26C5) || v == 0x26CE || v == 0x26D4 || v == 0x26EA || (v >= 0x26F2 && v <= 0x26F3) ||
        v == 0x26F5 || v == 0x26FA || v == 0x26FD || v == 0x2705 || (v >= 0x270A && v <= 0x270B) || v == 0x2728 ||
        v == 0x274C || v == 0x274E || (v >= 0x2753 && v <= 0x2755) || v == 0x2757 || (v >= 0x2795 && v <= 0x2797) ||
        v == 0x27B0 || v == 0x27BF || (v >= 0x2B1B && v <= 0x2B1C) || v == 0x2B50 || v == 0x2B55 ||
        (v >= 0x2E80 && v <= 0x303E) || (v >= 0x3041 && v <= 0x33FF) || (v >= 0x3400 && v <= 0x4DBF) ||
        (v >= 0x4E00 && v <= 0x9FFF) || (v >= 0xA000 && v <= 0xA4CF) || (v >= 0xA960 && v <= 0xA97F) ||
        (v >= 0xAC00 && v <= 0xD7A3) || (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFE10 && v <= 0xFE19) ||
        (v >= 0xFE30 && v <= 0xFE6F) || (v >= 0xFF00 && v <= 0xFF60) || (v >= 0xFFE0 && v <= 0xFFE6) ||
        v == 0x1F004 || v == 0x1F0CF || v == 0x1F18E || (v >= 0x1F191 && v <= 0x1F19A) ||
        (v >= 0x1F200 && v <= 0x1F2FF) || (v >= 0x1F300 && v <= 0x1F64F) || (v >= 0x1F680 && v <= 0x1F6FF) ||
        (v >= 0x1F7E0 && v <= 0x1F7EB) || (v >= 0x1F90C && v <= 0x1F9FF) || (v >= 0x1FA70 && v <= 0x1FAFF) ||
        (v >= 0x20000 && v <= 0x3FFFD);

    /// <summary>Szerokość pojedynczego grafemu (klastra znaków).</summary>
    public static int GraphemeWidth(ReadOnlySpan<char> g)
    {
        if (g.Length == 1)
        {
            char c = g[0];
            if (c >= 0x20 && c < 0x7F) return 1;
        }
        int width = 0;
        bool first = true;
        int riCount = 0;
        foreach (var r in g.EnumerateRunes())
        {
            if (r.Value == 0xFE0F) return 2;         // prezentacja emoji
            if (r.Value == 0x200D) return 2;         // sekwencja ZWJ
            if (r.Value >= 0x1F1E6 && r.Value <= 0x1F1FF) riCount++;
            if (first) { width = RuneWidth(r); first = false; }
        }
        if (riCount >= 1) return 2;
        return width;
    }

    /// <summary>Zwraca grafemy wraz z ich szerokościami.</summary>
    public static IEnumerable<(string Text, int Width)> Graphemes(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c < 0x80 && (i + 1 >= s.Length || s[i + 1] < 0x300))
            {
                if (c < 0x20 || c == 0x7F) yield return (" ", 1);
                else yield return (AsciiStrings[c], 1);
                i++;
                continue;
            }
            int len = StringInfo.GetNextTextElementLength(s, i);
            if (len <= 0) len = 1;
            var g = s.Substring(i, len);
            int w = GraphemeWidth(g);
            if (w == 0 && g.Length == 1 && char.IsControl(g[0])) { g = " "; w = 1; }
            yield return (g, w);
            i += len;
        }
    }

    public static int Width(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        bool ascii = true;
        foreach (char c in s) if (c >= 0x7F || c < 0x20) { ascii = false; break; }
        if (ascii) return s.Length;
        int w = 0;
        foreach (var (_, gw) in Graphemes(s)) w += gw;
        return w;
    }

    /// <summary>Przycina tekst do szerokości, dodając „…” gdy się nie mieści.</summary>
    public static string Truncate(string? s, int maxWidth)
    {
        if (string.IsNullOrEmpty(s) || maxWidth <= 0) return "";
        if (Width(s) <= maxWidth) return s;
        var sb = new StringBuilder();
        int w = 0;
        foreach (var (g, gw) in Graphemes(s))
        {
            if (w + gw > maxWidth - 1) break;
            sb.Append(g);
            w += gw;
        }
        sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Usuwa znaki sterujące i normalizuje białe znaki do pojedynczych spacji.</summary>
    public static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Zawija tekst do podanej szerokości (z zachowaniem nowych linii).</summary>
    public static List<string> Wrap(string? text, int width, int maxLines = int.MaxValue)
    {
        var lines = new List<string>();
        if (width <= 0) return lines;
        if (string.IsNullOrEmpty(text)) return lines;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (lines.Count >= maxLines) break;
            var line = rawLine.Replace('\t', ' ');
            if (line.Length == 0) { lines.Add(""); continue; }

            var cur = new StringBuilder();
            int curW = 0;
            var word = new StringBuilder();
            int wordW = 0;

            void FlushWord()
            {
                if (word.Length == 0) return;
                if (curW + wordW <= width)
                {
                    cur.Append(word);
                    curW += wordW;
                }
                else
                {
                    if (cur.Length > 0)
                    {
                        lines.Add(cur.ToString().TrimEnd());
                        cur.Clear();
                        curW = 0;
                    }
                    // Słowo dłuższe niż linia — dzielimy twardo.
                    if (wordW > width)
                    {
                        foreach (var (g, gw) in Graphemes(word.ToString()))
                        {
                            if (curW + gw > width)
                            {
                                lines.Add(cur.ToString());
                                cur.Clear();
                                curW = 0;
                            }
                            cur.Append(g);
                            curW += gw;
                        }
                    }
                    else
                    {
                        var ws = word.ToString().TrimStart();
                        cur.Append(ws);
                        curW = Width(ws);
                    }
                }
                word.Clear();
                wordW = 0;
            }

            foreach (var (g, gw) in Graphemes(line))
            {
                word.Append(g);
                wordW += gw;
                if (g == " ") FlushWord();
            }
            FlushWord();
            lines.Add(cur.ToString().TrimEnd());
        }

        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        }
        return lines;
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string FormatSeconds(double s) => FormatTime(TimeSpan.FromSeconds(s));

    /// <summary>Parsuje czas w formacie "1:23:45" / "12:34" do sekund.</summary>
    public static int ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        int total = 0;
        foreach (var part in s.Trim().Split(':'))
        {
            if (!int.TryParse(part.Trim(), out int v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }
}
