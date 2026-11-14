namespace YoutubeTerminal.Tui;

[Flags]
internal enum Attr : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Reverse = 16,
}

internal readonly record struct Style(int Fg, int Bg, Attr Attr = Attr.None)
{
    public Style WithFg(int fg) => this with { Fg = fg };
    public Style WithBg(int bg) => this with { Bg = bg };
    public Style With(Attr a) => this with { Attr = Attr | a };
}

internal struct Cell
{
    /// <summary>Tekst grafemu; null = druga połowa szerokiego znaku.</summary>
    public string? Text;
    public int Fg;
    public int Bg;
    public Attr Attr;

    public bool SameAs(in Cell o) => Fg == o.Fg && Bg == o.Bg && Attr == o.Attr && string.Equals(Text, o.Text);
}

internal readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public Rect Inset(int dx, int dy) => new(X + dx, Y + dy, Math.Max(0, W - 2 * dx), Math.Max(0, H - 2 * dy));
    public Rect TakeTop(int h) => new(X, Y, W, Math.Min(h, H));
    public Rect SkipTop(int h) => new(X, Y + Math.Min(h, H), W, Math.Max(0, H - h));
    public Rect TakeLeft(int w) => new(X, Y, Math.Min(w, W), H);
    public Rect SkipLeft(int w) => new(X + Math.Min(w, W), Y, Math.Max(0, W - w), H);
    public Rect TakeBottom(int h) => new(X, Bottom - Math.Min(h, H), W, Math.Min(h, H));
    public Rect SkipBottom(int h) => new(X, Y, W, Math.Max(0, H - h));
}

/// <summary>Umieszczenie obrazu (Sixel lub półbloki) w prostokącie komórek.</summary>
internal sealed class ImagePlacement
{
    public Rect Area;
    /// <summary>Gotowe bajty do wysłania (zawierają pozycjonowanie). Null gdy obraz rysowany zewnętrznie.</summary>
    public byte[]? Data;
    /// <summary>Tożsamość obrazu — zmiana powoduje ponowne narysowanie.</summary>
    public object? Key;
    /// <summary>Obszar zarezerwowany dla wideo — rysowany przez wątek odtwarzacza.</summary>
    public bool External;
}

/// <summary>Bufor ekranu (tylny), do którego widoki rysują w każdej klatce.</summary>
internal sealed class ScreenBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Cell[] Cells { get; private set; } = [];
    public List<ImagePlacement> Images { get; } = new();
    private Rect _clip;

    public ScreenBuffer(int w, int h) => Resize(w, h);

    public void Resize(int w, int h)
    {
        Width = Math.Max(1, w);
        Height = Math.Max(1, h);
        Cells = new Cell[Width * Height];
        _clip = new Rect(0, 0, Width, Height);
    }

    public Rect Bounds => new(0, 0, Width, Height);

    public void Clear(Style s)
    {
        var c = new Cell { Text = " ", Fg = s.Fg, Bg = s.Bg, Attr = s.Attr };
        Array.Fill(Cells, c);
        Images.Clear();
        _clip = Bounds;
    }

    public IDisposable Clip(Rect r)
    {
        var prev = _clip;
        int x0 = Math.Max(prev.X, r.X), y0 = Math.Max(prev.Y, r.Y);
        int x1 = Math.Min(prev.Right, r.Right), y1 = Math.Min(prev.Bottom, r.Bottom);
        _clip = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        return new ClipScope(this, prev);
    }

    private sealed class ClipScope(ScreenBuffer b, Rect prev) : IDisposable
    {
        public void Dispose() => b._clip = prev;
    }

    public void Fill(Rect r, Style s, string ch = " ")
    {
        int x0 = Math.Max(r.X, _clip.X), y0 = Math.Max(r.Y, _clip.Y);
        int x1 = Math.Min(r.Right, _clip.Right), y1 = Math.Min(r.Bottom, _clip.Bottom);
        for (int y = y0; y < y1; y++)
        {
            int row = y * Width;
            for (int x = x0; x < x1; x++)
            {
                ref var c = ref Cells[row + x];
                c.Text = ch; c.Fg = s.Fg; c.Bg = s.Bg; c.Attr = s.Attr;
            }
        }
    }

    public void SetBg(Rect r, int bg)
    {
        int x0 = Math.Max(r.X, _clip.X), y0 = Math.Max(r.Y, _clip.Y);
        int x1 = Math.Min(r.Right, _clip.Right), y1 = Math.Min(r.Bottom, _clip.Bottom);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                Cells[y * Width + x].Bg = bg;
    }

    public void Put(int x, int y, string text, Style s)
    {
        if (!_clip.Contains(x, y)) return;
        ref var c = ref Cells[y * Width + x];
        c.Text = text; c.Fg = s.Fg; c.Bg = s.Bg; c.Attr = s.Attr;
    }

    /// <summary>Rysuje tekst w jednej linii, zwraca liczbę zajętych kolumn.</summary>
    public int Text(int x, int y, string? text, Style s, int maxWidth = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text) || y < _clip.Y || y >= _clip.Bottom) return 0;
        int limit = Math.Min(_clip.Right, maxWidth == int.MaxValue ? int.MaxValue : x + maxWidth);
        int cx = x;
        foreach (var (g, gw) in TextUtil.Graphemes(text))
        {
            if (gw == 0)
            {
                continue;
            }
            if (cx + gw > limit) break;
            if (cx >= _clip.X)
            {
                ref var c = ref Cells[y * Width + cx];
                // Nadpisanie drugiej połowy szerokiego znaku — czyścimy jego początek.
                if (c.Text == null && cx > 0) Cells[y * Width + cx - 1].Text = " ";
                c.Text = g; c.Fg = s.Fg; c.Bg = s.Bg; c.Attr = s.Attr;
                if (gw == 2)
                {
                    if (cx + 1 < Width)
                    {
                        ref var c2 = ref Cells[y * Width + cx + 1];
                        c2.Text = null; c2.Fg = s.Fg; c2.Bg = s.Bg; c2.Attr = s.Attr;
                    }
                }
                else if (cx + 1 < Width && Cells[y * Width + cx + 1].Text == null)
                {
                    Cells[y * Width + cx + 1].Text = " ";
                }
            }
            cx += gw;
        }
        return cx - x;
    }

    /// <summary>Tekst przycięty z „…”.</summary>
    public int TextTrunc(int x, int y, string? text, Style s, int maxWidth)
    {
        if (maxWidth <= 0) return 0;
        return Text(x, y, TextUtil.Truncate(text, maxWidth), s, maxWidth);
    }

    public void TextRight(int right, int y, string? text, Style s)
    {
        int w = TextUtil.Width(text);
        Text(right - w, y, text, s);
    }

    public void TextCenter(Rect r, int y, string? text, Style s)
    {
        var t = TextUtil.Truncate(text, r.W);
        int w = TextUtil.Width(t);
        Text(r.X + Math.Max(0, (r.W - w) / 2), y, t, s);
    }

    public void HLine(int x, int y, int w, Style s, string ch = "─")
    {
        for (int i = 0; i < w; i++) Put(x + i, y, ch, s);
    }

    public void VLine(int x, int y, int h, Style s, string ch = "│")
    {
        for (int i = 0; i < h; i++) Put(x, y + i, ch, s);
    }

    public void Box(Rect r, Style s, string? title = null, Style? titleStyle = null)
    {
        if (r.W < 2 || r.H < 2) return;
        Put(r.X, r.Y, "╭", s);
        Put(r.Right - 1, r.Y, "╮", s);
        Put(r.X, r.Bottom - 1, "╰", s);
        Put(r.Right - 1, r.Bottom - 1, "╯", s);
        HLine(r.X + 1, r.Y, r.W - 2, s);
        HLine(r.X + 1, r.Bottom - 1, r.W - 2, s);
        VLine(r.X, r.Y + 1, r.H - 2, s);
        VLine(r.Right - 1, r.Y + 1, r.H - 2, s);
        if (!string.IsNullOrEmpty(title))
            TextTrunc(r.X + 2, r.Y, " " + title + " ", titleStyle ?? s, r.W - 4);
    }

    /// <summary>Rysuje akapity z zawijaniem; zwraca liczbę użytych linii.</summary>
    public int Paragraph(Rect r, string? text, Style s, int skipLines = 0)
    {
        var lines = TextUtil.Wrap(text, r.W);
        int y = r.Y;
        for (int i = skipLines; i < lines.Count && y < r.Bottom; i++, y++)
            Text(r.X, y, lines[i], s, r.W);
        return y - r.Y;
    }

    public void AddImage(ImagePlacement p)
    {
        // Obrazy poza obszarem przycięcia są pomijane (np. przy przewijaniu listy).
        var a = p.Area;
        if (a.X < _clip.X || a.Y < _clip.Y || a.Right > _clip.Right || a.Bottom > _clip.Bottom) return;
        if (a.IsEmpty) return;
        Images.Add(p);
    }
}
