using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class VtParserTests
{
    private static List<InputEvent> Parse(string input, bool final = true)
    {
        var events = new List<InputEvent>();
        new VtParser().Parse(input, final, events);
        return events;
    }

    private static KeyEvent SingleKey(string input) => Assert.IsType<KeyEvent>(Assert.Single(Parse(input)));

    [Theory]
    [InlineData("\x1b[A", Key.Up)]
    [InlineData("\x1b[B", Key.Down)]
    [InlineData("\x1b[C", Key.Right)]
    [InlineData("\x1b[D", Key.Left)]
    [InlineData("\x1b[H", Key.Home)]
    [InlineData("\x1b[F", Key.End)]
    [InlineData("\x1bOA", Key.Up)]
    [InlineData("\x1bOP", Key.F1)]
    [InlineData("\x1bOS", Key.F4)]
    [InlineData("\x1b[2~", Key.Insert)]
    [InlineData("\x1b[3~", Key.Delete)]
    [InlineData("\x1b[5~", Key.PageUp)]
    [InlineData("\x1b[6~", Key.PageDown)]
    [InlineData("\x1b[1~", Key.Home)]
    [InlineData("\x1b[4~", Key.End)]
    [InlineData("\x1b[15~", Key.F5)]
    [InlineData("\x1b[24~", Key.F12)]
    [InlineData("\x1b[Z", Key.BackTab)]
    [InlineData("\r", Key.Enter)]
    [InlineData("\t", Key.Tab)]
    [InlineData("\x7f", Key.Backspace)]
    public void SpecialKeys(string input, object expected)
    {
        Assert.Equal((Key)expected, SingleKey(input).Key);
    }

    [Theory]
    [InlineData("\x1b[1;2C", Key.Right, Mods.Shift)]
    [InlineData("\x1b[1;5D", Key.Left, Mods.Ctrl)]
    [InlineData("\x1b[1;3A", Key.Up, Mods.Alt)]
    [InlineData("\x1b[1;6B", Key.Down, Mods.Shift | Mods.Ctrl)]
    [InlineData("\x1b[5;5~", Key.PageUp, Mods.Ctrl)]
    public void ModifiedKeys(string input, object key, object mods)
    {
        var k = SingleKey(input);
        Assert.Equal((Key)key, k.Key);
        Assert.Equal((Mods)mods, k.Mods);
    }

    [Fact]
    public void PlainAndUnicodeCharacters()
    {
        var events = Parse("aą Z").Cast<KeyEvent>().ToList();
        Assert.Equal(['a', 'ą', ' ', 'Z'], events.Select(e => e.Char));
        Assert.All(events, e => Assert.Equal(Key.Char, e.Key));
    }

    [Fact]
    public void SurrogatePairBecomesTwoCharEvents()
    {
        var events = Parse("😀").Cast<KeyEvent>().ToList();
        Assert.Equal(2, events.Count);
        Assert.True(char.IsHighSurrogate(events[0].Char));
        Assert.True(char.IsLowSurrogate(events[1].Char));
    }

    [Fact]
    public void ControlLetters()
    {
        var k = SingleKey("\x01");
        Assert.Equal(Key.Char, k.Key);
        Assert.Equal('a', k.Char);
        Assert.Equal(Mods.Ctrl, k.Mods);
        Assert.True(k.IsCtrl('a'));
    }

    [Fact]
    public void CtrlBackspace()
    {
        var k = SingleKey("\b");
        Assert.Equal(Key.Backspace, k.Key);
        Assert.Equal(Mods.Ctrl, k.Mods);
    }

    [Fact]
    public void AltLetter()
    {
        var k = SingleKey("\x1bx");
        Assert.Equal('x', k.Char);
        Assert.Equal(Mods.Alt, k.Mods);
    }

    [Fact]
    public void LoneEscapeWaitsForMoreUnlessFinal()
    {
        var events = new List<InputEvent>();
        var parser = new VtParser();
        Assert.Equal(0, parser.Parse("\x1b", final: false, events));
        Assert.Empty(events);
        Assert.Equal(1, parser.Parse("\x1b", final: true, events));
        Assert.Equal(Key.Escape, Assert.IsType<KeyEvent>(Assert.Single(events)).Key);
    }

    [Fact]
    public void IncompleteCsiIsKeptForNextChunk()
    {
        var events = new List<InputEvent>();
        var parser = new VtParser();
        Assert.Equal(1, parser.Parse("a\x1b[1;5", final: false, events));
        Assert.Single(events);
    }

    [Fact]
    public void DoubleEscapeIsEscapeKey()
    {
        var events = Parse("\x1b\x1b");
        Assert.All(events, e => Assert.Equal(Key.Escape, Assert.IsType<KeyEvent>(e).Key));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void SgrMousePressReleaseWheelMove()
    {
        var press = Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<0;10;5M")));
        Assert.Equal(MouseKind.Press, press.Kind);
        Assert.Equal(0, press.Button);
        Assert.Equal((9, 4), (press.X, press.Y));

        Assert.Equal(MouseKind.Release, Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<0;10;5m"))).Kind);
        Assert.Equal(MouseKind.WheelUp, Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<64;1;1M"))).Kind);
        Assert.Equal(MouseKind.WheelDown, Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<65;1;1M"))).Kind);
        Assert.Equal(MouseKind.Move, Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<32;3;3M"))).Kind);

        var right = Assert.IsType<MouseEvent>(Assert.Single(Parse("\x1b[<18;1;1M")));
        Assert.Equal(2, right.Button);
        Assert.Equal(Mods.Ctrl, right.Mods);
    }

    [Fact]
    public void BracketedPaste()
    {
        var paste = Assert.IsType<PasteEvent>(Assert.Single(Parse("\x1b[200~https://youtu.be/abc\x1b[201~")));
        Assert.Equal("https://youtu.be/abc", paste.Text);
    }

    [Fact]
    public void BracketedPasteSplitAcrossChunks()
    {
        var parser = new VtParser();
        var events = new List<InputEvent>();
        const string chunk1 = "\x1b[200~hel";
        int used = parser.Parse(chunk1, final: false, events);
        string rest = chunk1[used..] + "lo\x1b[201~x";
        parser.Parse(rest, final: true, events);
        Assert.Equal("hello", Assert.IsType<PasteEvent>(events[0]).Text);
        Assert.Equal('x', Assert.IsType<KeyEvent>(events[1]).Char);
    }

    [Fact]
    public void DeviceAttributesReply()
    {
        var r = Assert.IsType<ReplyEvent>(Assert.Single(Parse("\x1b[?61;4;6;7c")));
        Assert.Equal('?', r.Prefix);
        Assert.Equal('c', r.Final);
        Assert.Equal([61, 4, 6, 7], r.Params);
    }

    [Fact]
    public void CellSizeReply()
    {
        var r = Assert.IsType<ReplyEvent>(Assert.Single(Parse("\x1b[6;20;10t")));
        Assert.Equal('t', r.Final);
        Assert.Equal([6, 20, 10], r.Params);
    }

    [Fact]
    public void OscSequenceIsSkipped()
    {
        var k = Assert.IsType<KeyEvent>(Assert.Single(Parse("\x1b]11;rgb:0000/0000/0000\aa")));
        Assert.Equal('a', k.Char);
    }

    [Fact]
    public void KeyEventHelpers()
    {
        var k = new KeyEvent(Key.Char, 'q');
        Assert.True(k.Is('q'));
        Assert.False(new KeyEvent(Key.Char, 'q', Mods.Alt).Is('q'));
    }
}
