using System.Text;
using YoutubeTerminal.Graphics;
using YoutubeTerminal.Tests.Helpers;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class EncoderTests
{
    private static Bitmap Quadrants(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.Pixels[y * w + x] = (x < w / 2, y < h / 2) switch
                {
                    (true, true) => Bitmap.Rgb(255, 0, 0),
                    (false, true) => Bitmap.Rgb(0, 255, 0),
                    (true, false) => Bitmap.Rgb(0, 0, 255),
                    _ => Bitmap.Rgb(255, 255, 255),
                };
        return bmp;
    }

    private static ImageEncoder Sixel() => new() { Mode = RenderMode.Sixel, CellWidth = 10, CellHeight = 20 };

    [Theory]
    [InlineData(40, 30)]
    [InlineData(37, 32)]   // wysokość nie jest wielokrotnością 6 (ostatnie pasmo niepełne)
    [InlineData(1, 1)]
    public void Sixel_DecodesBackToSameImage(int w, int h)
    {
        var bmp = Quadrants(w, h);
        var data = Sixel().Encode(bmp, new Rect(3, 2, 5, 2));
        var dec = SixelDecoder.Decode(data);
        Assert.Equal((w, h), (dec.Width, dec.Height));
        for (int i = 0; i < bmp.Pixels.Length; i++)
            Assert.Equal(bmp.Pixels[i], dec.Pixels[i]);
    }

    [Fact]
    public void Sixel_HeaderPositionAndTerminator()
    {
        var data = Sixel().Encode(Quadrants(40, 30), new Rect(3, 2, 5, 2));
        var s = Encoding.ASCII.GetString(data);
        Assert.StartsWith("\x1b[3;4H\x1bP0;1;0q\"1;1;40;30", s);
        Assert.EndsWith("\x1b\\", s);
        Assert.NotEqual('-', s[^3]); // brak „nowej linii” po ostatnim paśmie
    }

    [Fact]
    public void Sixel_UsesRunLengthEncodingForFlatAreas()
    {
        var flat = new Bitmap(600, 6);
        flat.Fill(Bitmap.Rgb(0, 0, 0));
        var data = Sixel().Encode(flat, new Rect(0, 0, 60, 1));
        Assert.Contains("!600~", Encoding.ASCII.GetString(data));
        Assert.True(data.Length < 100);
    }

    [Fact]
    public void Sixel_FixedPaletteWithDitherStillDecodes()
    {
        var enc = Sixel();
        enc.AdaptivePalette = false;
        enc.Dither = true;
        var dec = SixelDecoder.Decode(enc.Encode(Quadrants(24, 24), new Rect(0, 0, 3, 2)));
        Assert.Equal((24, 24), (dec.Width, dec.Height));
        Assert.DoesNotContain(0u, dec.Pixels); // każdy piksel ma przypisany kolor
    }

    [Fact]
    public void Layout_SixelFitsAreaAndCenters()
    {
        var l = Sixel().Layout(1280, 720, new Rect(10, 5, 10, 5));
        Assert.Equal(100, l.PixelWidth);
        Assert.Equal(54, l.PixelHeight);          // 56 zaokrąglone w dół do wielokrotności 6
        Assert.Equal(new Rect(10, 6, 10, 3), l.Cells);
    }

    [Fact]
    public void Layout_SixelScaleShrinksImage()
    {
        var full = Sixel().Layout(1920, 1080, new Rect(0, 0, 100, 40));
        var half = Sixel().Layout(1920, 1080, new Rect(0, 0, 100, 40), scale: 0.5);
        Assert.True(half.PixelWidth <= full.PixelWidth / 2 + 1);
        Assert.Equal(0, half.PixelHeight % 6);
    }

    [Fact]
    public void Layout_HalfBlockUsesTwoPixelsPerCell()
    {
        var enc = new ImageEncoder { Mode = RenderMode.HalfBlock, CellWidth = 10, CellHeight = 20 };
        var l = enc.Layout(200, 100, new Rect(0, 0, 20, 10));
        Assert.Equal((20, 10), (l.PixelWidth, l.PixelHeight));
        Assert.Equal(new Rect(0, 2, 20, 5), l.Cells);
    }

    /// <summary>Poziome paski co 1 px: czerwony / niebieski — każda komórka półbloku ma różne kolory góra/dół.</summary>
    private static Bitmap Stripes(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.Pixels[y * w + x] = y % 2 == 0 ? Bitmap.Rgb(255, 0, 0) : Bitmap.Rgb(0, 0, 255);
        return bmp;
    }

    [Fact]
    public void HalfBlock_EmitsUpperHalfBlocksPerRow()
    {
        var enc = new ImageEncoder { Mode = RenderMode.HalfBlock };
        var data = enc.Encode(Stripes(4, 4), new Rect(5, 7, 4, 2));
        var s = Encoding.UTF8.GetString(data);
        Assert.Equal(8, s.Count(c => c == '▀'));
        Assert.Contains("\x1b[38;2;252;0;0m", s);   // kolor góry (czerwony, 6 bitów na kanał)
        Assert.Contains("\x1b[48;2;0;0;252m", s);   // kolor dołu (niebieski)
        Assert.Equal([(5, 7), (5, 8)], SixelDecoder.CursorMoves(data));
        Assert.EndsWith("\x1b[0m", s);
    }

    [Fact]
    public void HalfBlock_SameColorTopAndBottomUsesSpace()
    {
        var enc = new ImageEncoder { Mode = RenderMode.HalfBlock };
        var s = Encoding.UTF8.GetString(enc.Encode(Quadrants(4, 4), new Rect(0, 0, 4, 2)));
        Assert.DoesNotContain("▀", s);
        Assert.Equal(8, s.Count(c => c == ' '));
    }

    [Fact]
    public void VideoFrame_IsSplitIntoPositionedStrips()
    {
        var enc = Sixel();
        var bmp = Quadrants(100, 120);
        var frame = enc.EncodeVideoFrame(bmp.Pixels, 100, 120, new Rect(2, 4, 10, 6));

        // wysokość komórki 20 px -> paski po 3 wiersze (60 px, wielokrotność 6)
        Assert.Equal(2, frame.Strips.Length);
        Assert.Equal(2, frame.Signatures.Length);
        Assert.Equal((2, 4), SixelDecoder.CursorMoves(frame.Strips[0])[0]);
        Assert.Equal((2, 7), SixelDecoder.CursorMoves(frame.Strips[1])[0]);

        // dolny pasek to dolna połowa obrazu
        var bottom = SixelDecoder.Decode(frame.Strips[1]);
        Assert.Equal((100, 60), (bottom.Width, bottom.Height));
        Assert.Equal(Bitmap.Rgb(0, 0, 255), bottom.Pixels[0]);
        Assert.Equal(Bitmap.Rgb(255, 255, 255), bottom.Pixels[99]);
        Assert.Equal(frame.Strips.Sum(s => s.Length), frame.TotalBytes);
    }

    [Fact]
    public void VideoFrame_SameImageGivesSimilarSignatures()
    {
        var enc = Sixel();
        var bmp = Quadrants(100, 120);
        var a = enc.EncodeVideoFrame(bmp.Pixels, 100, 120, new Rect(0, 0, 10, 6));
        var b = enc.EncodeVideoFrame((uint[])bmp.Pixels.Clone(), 100, 120, new Rect(0, 0, 10, 6));
        Assert.True(ImageEncoder.SimilarSignature(a.Signatures[0], b.Signatures[0]));

        // zmiana w dolnej połowie nie wpływa na sygnaturę górnego paska
        var changed = (uint[])bmp.Pixels.Clone();
        for (int i = 60 * 100; i < changed.Length; i++) changed[i] = Bitmap.Rgb(30, 30, 30);
        var c = enc.EncodeVideoFrame(changed, 100, 120, new Rect(0, 0, 10, 6));
        Assert.True(ImageEncoder.SimilarSignature(a.Signatures[0], c.Signatures[0]));
        Assert.False(ImageEncoder.SimilarSignature(a.Signatures[1], c.Signatures[1]));
    }

    [Fact]
    public void SimilarSignature_Tolerance()
    {
        byte[] a = [100, 100, 100];
        Assert.True(ImageEncoder.SimilarSignature(a, [105, 95, 100]));
        Assert.False(ImageEncoder.SimilarSignature(a, [106, 100, 100]));
        Assert.False(ImageEncoder.SimilarSignature(null, a));
        Assert.False(ImageEncoder.SimilarSignature(a, [100, 100]));
    }

    [Fact]
    public void VideoFrame_HalfBlockStrips()
    {
        var enc = new ImageEncoder { Mode = RenderMode.HalfBlock };
        var bmp = Quadrants(8, 8);
        var frame = enc.EncodeVideoFrame(bmp.Pixels, 8, 8, new Rect(0, 0, 8, 4));
        Assert.Equal(2, frame.Strips.Length); // po 2 wiersze komórek (4 px)
        Assert.Equal([(0, 0), (0, 1)], SixelDecoder.CursorMoves(frame.Strips[0]));
    }
}
