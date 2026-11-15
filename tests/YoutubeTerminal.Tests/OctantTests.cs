using System.Text;
using YoutubeTerminal.Graphics;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class OctantTests
{
    [Fact]
    public void Table_UsesWholeUnicodeOctantBlock()
    {
        _ = Octants.Glyphs;
        Assert.Equal(230, Octants.BlockGlyphCount); // U+1CD00–U+1CDE5
    }

    [Fact]
    public void Table_EveryPatternIsDrawable()
    {
        // Każdy wzór ma znak albo jego dopełnienie ma znak (rysowane z zamienionymi kolorami).
        for (int p = 0; p < 256; p++)
            Assert.True(Octants.Glyphs[p] != null || Octants.Glyphs[~p & 0xFF] != null, $"wzór 0x{p:X2}");
    }

    [Theory]
    [InlineData(0x04, 0x1CD00)] // BLOCK OCTANT-3
    [InlineData(0x06, 0x1CD01)] // BLOCK OCTANT-23
    [InlineData(0x10, 0x1CD09)] // BLOCK OCTANT-5
    [InlineData(0x0F, 0x2580)]  // górna połowa ▀
    [InlineData(0x55, 0x258C)]  // lewa połowa ▌
    [InlineData(0xA5, 0x259A)]  // ▚
    public void Table_KnownCodePoints(int pattern, int codePoint)
    {
        var glyph = Octants.Glyphs[pattern];
        Assert.NotNull(glyph);
        Assert.Equal(codePoint, Rune.GetRuneAt(Encoding.UTF8.GetString(glyph!), 0).Value);
    }

    [Fact]
    public void Fit_TwoColorCellIsExact()
    {
        // Górne 4 piksele czerwone, dolne niebieskie → wzór 0x0F.
        uint[] px = [0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF, 0x0000FF, 0x0000FF];
        int mask = Octants.Fit(px, out uint c1, out uint c0);
        uint set = mask == 0x0F ? c1 : c0, unset = mask == 0x0F ? c0 : c1;
        Assert.True(mask is 0x0F or 0xF0);
        Assert.Equal(0xFF0000u, set);
        Assert.Equal(0x0000FFu, unset);
    }

    [Fact]
    public void Fit_UniformCellIsSolid()
    {
        uint[] px = [0x808080, 0x818181, 0x808080, 0x7F7F7F, 0x808080, 0x808080, 0x818181, 0x808080];
        Assert.Equal(0, Octants.Fit(px, out _, out _));
    }

    [Fact]
    public void Layout_OctantUsesTwoByFourPixelsPerCell()
    {
        var enc = new ImageEncoder { Mode = RenderMode.Octant, CellWidth = 10, CellHeight = 20 };
        var l = enc.Layout(1920, 1080, new Rect(0, 0, 80, 24));
        Assert.Equal(0, l.PixelWidth % 2);
        Assert.Equal(0, l.PixelHeight % 4);
        Assert.Equal(l.PixelWidth / 2, l.Cells.W);
        Assert.Equal(l.PixelHeight / 4, l.Cells.H);
        Assert.Equal(80, l.Cells.W);
        Assert.InRange(l.PixelWidth / (double)l.PixelHeight, 16 / 9.0 * 0.95, 16 / 9.0 * 1.05);
    }

    [Fact]
    public void Encode_RoundTripsTwoColorCells()
    {
        // Obraz 8×8 px (4×2 komórki): w każdej komórce dowolny wzór z dwóch kolorów.
        const int w = 8, h = 8;
        var rnd = new Random(7);
        var px = new uint[w * h];
        uint[] palette = [0x101010, 0xF0F0F0, 0xC02020, 0x2040C0, 0x20A040];
        for (int cy = 0; cy < h / 4; cy++)
        for (int cx = 0; cx < w / 2; cx++)
        {
            uint a = palette[rnd.Next(palette.Length)], b = palette[rnd.Next(palette.Length)];
            for (int k = 0; k < 8; k++)
                px[(cy * 4 + k / 2) * w + cx * 2 + k % 2] = rnd.Next(2) == 0 ? a : b;
        }
        var enc = new ImageEncoder { Mode = RenderMode.Octant };
        var bytes = enc.Encode(px, w, h, new Rect(3, 5, w / 2, h / 4));
        var decoded = OctantDecoder.Decode(bytes, 3, 5, w / 2, h / 4);
        for (int i = 0; i < px.Length; i++)
            Assert.Equal(px[i] & 0xFCFCFC, decoded[i]);
    }

    [Fact]
    public void VideoFrame_OctantStripsCoverWholeImage()
    {
        var enc = new ImageEncoder { Mode = RenderMode.Octant };
        const int w = 40, h = 24; // 20×6 komórek → paski po 2 wiersze komórek (8 px)
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (uint)(i * 2654435761u) & 0xFFFFFF;
        var frame = enc.EncodeVideoFrame(px, w, h, new Rect(0, 0, w / 2, h / 4));
        Assert.Equal(3, frame.Strips.Length);
        Assert.All(frame.Strips, s => Assert.StartsWith("\x1b[", Encoding.ASCII.GetString(s, 0, 2)));
    }

    /// <summary>Minimalny dekoder wyjścia oktantów: pozycjonowanie, kolory SGR i znaki → piksele.</summary>
    private static class OctantDecoder
    {
        public static uint[] Decode(byte[] data, int cellX, int cellY, int cols, int rows)
        {
            var patterns = new Dictionary<int, int> { [' '] = 0 };
            for (int p = 0; p < 256; p++)
                if (Octants.Glyphs[p] is { } g && p != 0)
                    patterns[Rune.GetRuneAt(Encoding.UTF8.GetString(g), 0).Value] = p;

            var px = new uint[cols * 2 * rows * 4];
            string s = Encoding.UTF8.GetString(data);
            int x = 0, y = 0;
            uint fg = 0, bg = 0;
            for (int i = 0; i < s.Length;)
            {
                if (s[i] == '\x1b' && s[i + 1] == '[')
                {
                    int end = i + 2;
                    while (!char.IsLetter(s[end])) end++;
                    var args = s[(i + 2)..end].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    if (s[end] == 'H') { y = args[0] - 1; x = args[1] - 1; }
                    else if (s[end] == 'm')
                    {
                        for (int a = 0; a < args.Length; a++)
                        {
                            if (args[a] == 38) { fg = (uint)(args[a + 2] << 16 | args[a + 3] << 8 | args[a + 4]); a += 4; }
                            else if (args[a] == 48) { bg = (uint)(args[a + 2] << 16 | args[a + 3] << 8 | args[a + 4]); a += 4; }
                        }
                    }
                    i = end + 1;
                    continue;
                }
                var rune = Rune.GetRuneAt(s, i);
                i += rune.Utf16SequenceLength;
                int pattern = patterns[rune.Value];
                int ox = (x - cellX) * 2, oy = (y - cellY) * 4;
                for (int k = 0; k < 8; k++)
                    px[(oy + k / 2) * cols * 2 + ox + k % 2] = (pattern & (1 << k)) != 0 ? fg : bg;
                x++;
            }
            return px;
        }
    }
}
