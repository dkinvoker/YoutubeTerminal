using System.Globalization;
using System.Text;
using YoutubeTerminal.Graphics;

namespace YoutubeTerminal.Tui;

/// <summary>
/// Zrzut ekranu do SVG: komórki (tekst, kolory, atrybuty) oraz obrazy jako osadzone PNG.
/// Służy do ilustracji w dokumentacji — wynik wygląda jak terminal, ale nie zależy od jego czcionki i grafiki.
/// </summary>
internal static class SvgExport
{
    public const int CellW = 9, CellH = 19;
    private const double FontSize = 15;

    /// <summary>Obraz dla umieszczenia: piksele, rozmiar i czy jest przycięty do obszaru (cover).</summary>
    public delegate (uint[] Pixels, int Width, int Height, bool Cover)? ImageSource(ImagePlacement placement);

    public static string Render(ScreenBuffer s, int background, ImageSource images)
    {
        int w = s.Width * CellW, h = s.Height * CellH;
        var sb = new StringBuilder(1 << 20);
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" font-family=\"'Cascadia Mono','JetBrains Mono','SF Mono',Menlo,Consolas,'DejaVu Sans Mono',monospace\" font-size=\"{FontSize}\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{w}\" height=\"{h}\" rx=\"8\" fill=\"{Hex(background)}\"/>\n");

        // Tła: sąsiednie komórki o tym samym kolorze jako jeden prostokąt.
        for (int y = 0; y < s.Height; y++)
        {
            int x = 0;
            while (x < s.Width)
            {
                var c = s.Cells[y * s.Width + x];
                int bg = (c.Attr & Attr.Reverse) != 0 ? c.Fg : c.Bg;
                int start = x;
                while (x < s.Width)
                {
                    var d = s.Cells[y * s.Width + x];
                    int dbg = (d.Attr & Attr.Reverse) != 0 ? d.Fg : d.Bg;
                    if (dbg != bg) break;
                    x++;
                }
                if (bg != background)
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{start * CellW}\" y=\"{y * CellH}\" width=\"{(x - start) * CellW}\" height=\"{CellH}\" fill=\"{Hex(bg)}\"/>\n");
            }
        }

        // Obrazy pod tekstem (jak w terminalu: komórki pod obrazem są puste).
        foreach (var p in s.Images)
        {
            if (images(p) is not { } img || img.Width <= 0 || img.Height <= 0) continue;
            var png = Convert.ToBase64String(Png.Encode(img.Pixels, img.Width, img.Height));
            var r = p.Area;
            sb.Append(CultureInfo.InvariantCulture,
                $"<image x=\"{r.X * CellW}\" y=\"{r.Y * CellH}\" width=\"{r.W * CellW}\" height=\"{r.H * CellH}\" preserveAspectRatio=\"xMidYMid {(img.Cover ? "slice" : "meet")}\" href=\"data:image/png;base64,{png}\"/>\n");
        }

        // Tekst: ciągi komórek o tym samym stylu jako jeden <text> z pozycją każdego znaku (siatka terminala).
        for (int y = 0; y < s.Height; y++)
        {
            int baseline = y * CellH + (int)Math.Round(CellH * 0.76);
            int x = 0;
            while (x < s.Width)
            {
                var c = s.Cells[y * s.Width + x];
                if (string.IsNullOrEmpty(c.Text) || c.Text == " ") { x++; continue; }
                int fg = (c.Attr & Attr.Reverse) != 0 ? c.Bg : c.Fg;
                var attr = c.Attr & ~Attr.Reverse;
                var chars = new StringBuilder();
                var xs = new StringBuilder();
                while (x < s.Width)
                {
                    var d = s.Cells[y * s.Width + x];
                    int dfg = (d.Attr & Attr.Reverse) != 0 ? d.Bg : d.Fg;
                    if (string.IsNullOrEmpty(d.Text)) { x++; continue; } // druga połowa szerokiego znaku
                    if (d.Text == " ") break;
                    if (dfg != fg || (d.Attr & ~Attr.Reverse) != attr) break;
                    bool single = d.Text.Length == 1 && !char.IsSurrogate(d.Text[0]);
                    if (!single && chars.Length > 0) break; // znaki spoza BMP / klastry: osobny element
                    if (xs.Length > 0) xs.Append(' ');
                    xs.Append(x * CellW);
                    chars.Append(Escape(d.Text));
                    x++;
                    if (!single) break;
                }
                if (chars.Length == 0) { x++; continue; }
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{xs}\" y=\"{baseline}\" fill=\"{Hex(fg)}\"");
                if ((attr & Attr.Bold) != 0) sb.Append(" font-weight=\"bold\"");
                if ((attr & Attr.Italic) != 0) sb.Append(" font-style=\"italic\"");
                if ((attr & Attr.Underline) != 0) sb.Append(" text-decoration=\"underline\"");
                if ((attr & Attr.Dim) != 0) sb.Append(" opacity=\"0.7\"");
                sb.Append(" xml:space=\"preserve\">").Append(chars).Append("</text>\n");
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
