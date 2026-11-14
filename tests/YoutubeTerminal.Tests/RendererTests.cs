using System.Text;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class RendererTests
{
    private sealed class Capture : ITerminalOutput
    {
        public readonly List<string> Writes = new();
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(Encoding.UTF8.GetString(data));
        public string Last => Writes[^1];
    }

    private static readonly Style S = new(0xFFFFFF, 0x000000);

    private static ScreenBuffer Screen(int w = 8, int h = 3)
    {
        var b = new ScreenBuffer(w, h);
        b.Clear(S);
        return b;
    }

    [Fact]
    public void FirstFrameClearsAndDrawsEverything()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        b.Text(0, 0, "ab", S);
        r.Render(b, out_, syncOutput: false);

        Assert.True(r.FullRedrawHappened);
        Assert.Contains("\x1b[2J", out_.Last);
        Assert.Contains("ab", out_.Last);
    }

    [Fact]
    public void UnchangedFrameWritesNoCells()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        b.Text(0, 0, "ab", S);
        r.Render(b, out_, false);
        r.Render(b, out_, false);

        Assert.False(r.FullRedrawHappened);
        Assert.Equal("\x1b[0m", out_.Last);
    }

    [Fact]
    public void ChangedCellIsWrittenAtItsPosition()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        r.Render(b, out_, false);
        b.Text(3, 1, "X", S);
        r.Render(b, out_, false);

        Assert.Contains("\x1b[2;4H", out_.Last);
        Assert.Contains("X", out_.Last);
        Assert.DoesNotContain("\x1b[2J", out_.Last);
    }

    [Fact]
    public void SyncOutputWrapsFrame()
    {
        var out_ = new Capture();
        new Renderer().Render(Screen(), out_, syncOutput: true);
        Assert.StartsWith("\x1b[?2026h", out_.Last);
        Assert.EndsWith("\x1b[?2026l", out_.Last);
    }

    [Fact]
    public void ImageIsDrawnOnceAndCellsUnderItAreNotOverwritten()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var img = new ImagePlacement { Area = new Rect(2, 0, 3, 2), Data = Encoding.ASCII.GetBytes("<IMG>"), Key = "k" };

        var b = Screen();
        b.Text(0, 0, "abcdefgh", S);
        b.AddImage(img);
        r.Render(b, out_, false);
        Assert.Contains("<IMG>", out_.Last);
        Assert.True(out_.Last.IndexOf("<IMG>", StringComparison.Ordinal) > out_.Last.IndexOf("ab", StringComparison.Ordinal));

        // Ten sam obraz w kolejnej klatce: nie wysyłamy go ponownie ani nie piszemy tekstu w jego komórkach
        b = Screen();
        b.Text(0, 0, "abZZZfgh", S);
        b.AddImage(new ImagePlacement { Area = img.Area, Data = img.Data, Key = "k" });
        r.Render(b, out_, false);
        Assert.DoesNotContain("<IMG>", out_.Last);
        Assert.DoesNotContain("Z", out_.Last);
    }

    [Fact]
    public void RemovedImageIsErasedByRewritingCells()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        b.AddImage(new ImagePlacement { Area = new Rect(2, 1, 2, 1), Data = [1], Key = "k" });
        r.Render(b, out_, false);

        r.Render(Screen(), out_, false); // obraz zniknął
        Assert.Contains("\x1b[2;3H", out_.Last); // komórki pod obrazem zapisane ponownie
    }

    [Fact]
    public void ChangedImageKeyRedrawsImage()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        b.AddImage(new ImagePlacement { Area = new Rect(0, 0, 2, 1), Data = Encoding.ASCII.GetBytes("<A>"), Key = "a" });
        r.Render(b, out_, false);
        b = Screen();
        b.AddImage(new ImagePlacement { Area = new Rect(0, 0, 2, 1), Data = Encoding.ASCII.GetBytes("<B>"), Key = "b" });
        r.Render(b, out_, false);
        Assert.Contains("<B>", out_.Last);
    }

    [Fact]
    public void ExternalAreaReportsChangeOnlyWhenItAppears()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var video = new ImagePlacement { Area = new Rect(0, 0, 4, 2), External = true, Key = "video" };

        var b = Screen();
        b.AddImage(video);
        r.Render(b, out_, false);
        Assert.True(r.ExternalChanged);

        b = Screen();
        b.AddImage(new ImagePlacement { Area = video.Area, External = true, Key = "video" });
        r.Render(b, out_, false);
        Assert.False(r.ExternalChanged);
    }

    [Fact]
    public void InvalidateForcesFullRedraw()
    {
        var out_ = new Capture();
        var r = new Renderer();
        var b = Screen();
        r.Render(b, out_, false);
        r.Invalidate();
        r.Render(b, out_, false);
        Assert.True(r.FullRedrawHappened);
        Assert.Contains("\x1b[2J", out_.Last);
    }

    [Fact]
    public void ColorsAreEmittedAsTrueColor()
    {
        var out_ = new Capture();
        var b = new ScreenBuffer(2, 1);
        b.Clear(new Style(0x102030, 0x405060, Attr.Bold));
        new Renderer().Render(b, out_, false);
        Assert.Contains("\x1b[0;1;38;2;16;32;48;48;2;64;80;96m", out_.Last);
    }
}
