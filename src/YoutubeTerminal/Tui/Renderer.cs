using System.Text;

namespace YoutubeTerminal.Tui;

/// <summary>Bufor bajtów do budowania wyjścia terminala bez alokacji.</summary>
internal sealed class ByteBuilder
{
    private byte[] _buf;
    public int Length { get; private set; }

    public ByteBuilder(int capacity = 65536) => _buf = new byte[capacity];

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, Length);
    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    public void Clear() => Length = 0;

    public void Ensure(int extra)
    {
        if (Length + extra <= _buf.Length) return;
        int n = Math.Max(_buf.Length * 2, Length + extra);
        Array.Resize(ref _buf, n);
    }

    public void Append(byte b)
    {
        if (Length >= _buf.Length) Ensure(1);
        _buf[Length++] = b;
    }

    public void Append(ReadOnlySpan<byte> s)
    {
        Ensure(s.Length);
        s.CopyTo(_buf.AsSpan(Length));
        Length += s.Length;
    }

    public void AppendAscii(string s)
    {
        Ensure(s.Length);
        foreach (char c in s) _buf[Length++] = (byte)c;
    }

    public void AppendUtf8(string s)
    {
        Ensure(Encoding.UTF8.GetMaxByteCount(s.Length));
        Length += Encoding.UTF8.GetBytes(s, _buf.AsSpan(Length));
    }

    public void AppendInt(int v)
    {
        Ensure(11);
        if (v < 0) { _buf[Length++] = (byte)'-'; v = -v; }
        if (v < 10) { _buf[Length++] = (byte)('0' + v); return; }
        if (v < 100) { _buf[Length++] = (byte)('0' + v / 10); _buf[Length++] = (byte)('0' + v % 10); return; }
        if (v < 1000)
        {
            _buf[Length++] = (byte)('0' + v / 100);
            _buf[Length++] = (byte)('0' + v / 10 % 10);
            _buf[Length++] = (byte)('0' + v % 10);
            return;
        }
        Span<byte> tmp = stackalloc byte[11];
        int n = 0;
        while (v > 0) { tmp[n++] = (byte)('0' + v % 10); v /= 10; }
        while (n > 0) _buf[Length++] = tmp[--n];
    }

    /// <summary>CUP — przesunięcie kursora (0-based).</summary>
    public void MoveTo(int x, int y)
    {
        Append(0x1b); Append((byte)'[');
        AppendInt(y + 1); Append((byte)';'); AppendInt(x + 1); Append((byte)'H');
    }
}

/// <summary>
/// Renderer różnicowy: porównuje bufor tylny z tym, co jest na ekranie, i wysyła tylko zmiany.
/// Obsługuje obrazy (Sixel/półbloki) — komórki pod obrazem nie są nadpisywane tekstem,
/// a po zniknięciu obrazu są odświeżane (co go wymazuje).
/// </summary>
internal sealed class Renderer
{
    private const string ImageMarker = "\u0001img";
    private const string InvalidMarker = "\u0001inv";

    private Cell[] _front = [];
    private int _w, _h;
    private bool _full = true;
    private List<ImagePlacement> _prevImages = new();
    private readonly ByteBuilder _out = new(1 << 18);

    /// <summary>Ustawiane po pełnym przerysowaniu — odtwarzacz musi wtedy ponownie narysować klatkę.</summary>
    public bool FullRedrawHappened { get; private set; }

    /// <summary>Pojawił się nowy (lub przesunięty) obszar zewnętrzny — np. wideo musi narysować klatkę ponownie.</summary>
    public bool ExternalChanged { get; private set; }

    public void Invalidate() => _full = true;

    public void Render(ScreenBuffer back, Terminal term, bool syncOutput)
    {
        FullRedrawHappened = false;
        ExternalChanged = false;
        _out.Clear();
        if (syncOutput) _out.AppendAscii("\x1b[?2026h");

        if (back.Width != _w || back.Height != _h || _full)
        {
            _w = back.Width;
            _h = back.Height;
            _front = new Cell[_w * _h];
            Array.Fill(_front, new Cell { Text = InvalidMarker });
            _prevImages = new List<ImagePlacement>();
            _out.AppendAscii("\x1b[0m\x1b[2J");
            _full = false;
            FullRedrawHappened = true;
        }

        var images = back.Images;

        // 1. Obrazy, które zniknęły lub się zmieniły — unieważniamy komórki pod nimi.
        foreach (var prev in _prevImages)
        {
            if (!images.Any(i => SameImage(i, prev)))
                InvalidateRect(prev.Area);
        }

        // 2. Maska komórek zajętych przez bieżące obrazy: 1 = obraz już na ekranie (pomijamy),
        //    2 = nowy obraz (najpierw czyścimy komórki tłem, potem rysujemy obraz).
        byte[]? mask = null;
        if (images.Count > 0)
        {
            mask = new byte[_w * _h];
            foreach (var img in images)
            {
                bool existed = _prevImages.Any(p => SameImage(p, img));
                byte v = existed ? (byte)1 : (byte)2;
                var a = img.Area;
                for (int y = Math.Max(0, a.Y); y < Math.Min(_h, a.Bottom); y++)
                    for (int x = Math.Max(0, a.X); x < Math.Min(_w, a.Right); x++)
                        if (mask[y * _w + x] != 1) mask[y * _w + x] = v;
            }
        }

        // 3. Tekst.
        var cells = back.Cells;
        int curX = -1, curY = -1;
        int sFg = int.MinValue, sBg = int.MinValue;
        Attr sAttr = (Attr)0xFF;

        for (int y = 0; y < _h; y++)
        {
            int row = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int i = row + x;
                byte m = mask != null ? mask[i] : (byte)0;
                if (m == 1)
                {
                    _front[i] = new Cell { Text = ImageMarker };
                    continue;
                }
                if (m == 2) _front[i] = new Cell { Text = InvalidMarker };
                ref var c = ref cells[i];
                if (c.Text == null)
                {
                    // Druga połowa szerokiego znaku — obsłużona przez znak wiodący.
                    if (x > 0 && cells[i - 1].Text != null && !_front[i].SameAs(c)) _front[i] = c;
                    continue;
                }
                if (_front[i].SameAs(c)) continue;

                if (curX != x || curY != y)
                {
                    _out.MoveTo(x, y);
                    curX = x; curY = y;
                }
                if (c.Fg != sFg || c.Bg != sBg || c.Attr != sAttr)
                {
                    EmitSgr(c.Fg, c.Bg, c.Attr);
                    sFg = c.Fg; sBg = c.Bg; sAttr = c.Attr;
                }

                string text = c.Text;
                bool wide = x + 1 < _w && cells[i + 1].Text == null;
                if (text.Length == 1 && text[0] < 0x80) _out.Append((byte)text[0]);
                else _out.AppendUtf8(text);
                _front[i] = c;
                if (wide)
                {
                    _front[i + 1] = cells[i + 1];
                    // Szerokość w terminalu może się różnić od naszej — pozycję ustalamy na nowo.
                    curX = -1;
                }
                else if (text.Length > 1 || text[0] >= 0x80)
                {
                    curX = text.Length == 1 && text[0] < 0x2E80 ? x + 1 : -1;
                }
                else
                {
                    curX = x + 1;
                }
                if (m == 2)
                {
                    _front[i] = new Cell { Text = ImageMarker };
                    if (wide) _front[i + 1] = new Cell { Text = ImageMarker };
                }
            }
        }

        // 4. Obrazy nowe lub zmienione.
        _out.AppendAscii("\x1b[0m");
        foreach (var img in images)
        {
            bool existed = _prevImages.Any(p => SameImage(p, img));
            if (existed) continue;
            if (img.External) { ExternalChanged = true; continue; }
            if (img.Data == null) continue;
            _out.Append(img.Data);
        }
        _prevImages = images.Select(i => new ImagePlacement { Area = i.Area, Key = i.Key, External = i.External }).ToList();

        if (syncOutput) _out.AppendAscii("\x1b[?2026l");
        term.Write(_out.Span);
    }

    private static bool SameImage(ImagePlacement a, ImagePlacement b) =>
        a.Area == b.Area && a.External == b.External && Equals(a.Key, b.Key);

    private void InvalidateRect(Rect r)
    {
        for (int y = Math.Max(0, r.Y); y < Math.Min(_h, r.Bottom); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(_w, r.Right); x++)
                _front[y * _w + x] = new Cell { Text = InvalidMarker };
    }

    private void EmitSgr(int fg, int bg, Attr attr)
    {
        _out.AppendAscii("\x1b[0");
        if ((attr & Attr.Bold) != 0) _out.AppendAscii(";1");
        if ((attr & Attr.Dim) != 0) _out.AppendAscii(";2");
        if ((attr & Attr.Italic) != 0) _out.AppendAscii(";3");
        if ((attr & Attr.Underline) != 0) _out.AppendAscii(";4");
        if ((attr & Attr.Reverse) != 0) _out.AppendAscii(";7");
        if (fg >= 0)
        {
            _out.AppendAscii(";38;2;");
            _out.AppendInt((fg >> 16) & 0xFF); _out.Append((byte)';');
            _out.AppendInt((fg >> 8) & 0xFF); _out.Append((byte)';');
            _out.AppendInt(fg & 0xFF);
        }
        if (bg >= 0)
        {
            _out.AppendAscii(";48;2;");
            _out.AppendInt((bg >> 16) & 0xFF); _out.Append((byte)';');
            _out.AppendInt((bg >> 8) & 0xFF); _out.Append((byte)';');
            _out.AppendInt(bg & 0xFF);
        }
        _out.Append((byte)'m');
    }
}
