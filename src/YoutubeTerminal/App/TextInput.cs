using System.Globalization;
using System.Text;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.UI;

/// <summary>Jednoliniowe pole tekstowe z kursorem, edycją słowami i wklejaniem.</summary>
internal sealed class TextInput
{
    private string _text = "";
    private int _cursor; // indeks w łańcuchu (na granicy grafemu)
    private int _scroll;

    public string Placeholder = "";
    public event Action? Changed;

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            _cursor = _text.Length;
            Changed?.Invoke();
        }
    }

    private int PrevBoundary(int i)
    {
        if (i <= 0) return 0;
        int last = 0;
        var e = StringInfo.GetTextElementEnumerator(_text);
        while (e.MoveNext())
        {
            if (e.ElementIndex >= i) break;
            last = e.ElementIndex;
        }
        return last;
    }

    private int NextBoundary(int i)
    {
        if (i >= _text.Length) return _text.Length;
        return i + StringInfo.GetNextTextElementLength(_text, i);
    }

    private int WordLeft(int i)
    {
        while (i > 0 && char.IsWhiteSpace(_text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_text[i - 1])) i--;
        return i;
    }

    private int WordRight(int i)
    {
        while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;
        return i;
    }

    public void Insert(string s)
    {
        var clean = new StringBuilder();
        foreach (char c in s)
            if (!char.IsControl(c)) clean.Append(c);
            else if (c is '\n' or '\r' or '\t') clean.Append(' ');
        if (clean.Length == 0) return;
        _text = _text.Insert(_cursor, clean.ToString());
        _cursor += clean.Length;
        Changed?.Invoke();
    }

    private char _pendingHigh;

    public bool HandleKey(KeyEvent k)
    {
        bool ctrl = (k.Mods & Mods.Ctrl) != 0;
        switch (k.Key)
        {
            case Key.Char when ctrl:
                switch (char.ToLowerInvariant(k.Char))
                {
                    case 'a': _cursor = 0; return true;
                    case 'e': _cursor = _text.Length; return true;
                    case 'u': _text = _text[_cursor..]; _cursor = 0; Changed?.Invoke(); return true;
                    case 'k': _text = _text[.._cursor]; Changed?.Invoke(); return true;
                    case 'w':
                        int w = WordLeft(_cursor);
                        _text = _text.Remove(w, _cursor - w);
                        _cursor = w;
                        Changed?.Invoke();
                        return true;
                }
                return false;
            case Key.Char when (k.Mods & Mods.Alt) == 0:
                if (char.IsHighSurrogate(k.Char)) { _pendingHigh = k.Char; return true; }
                if (char.IsLowSurrogate(k.Char) && _pendingHigh != 0)
                {
                    Insert(new string([_pendingHigh, k.Char]));
                    _pendingHigh = '\0';
                    return true;
                }
                Insert(k.Char.ToString());
                return true;
            case Key.Backspace:
                if (_cursor == 0) return true;
                int from = ctrl ? WordLeft(_cursor) : PrevBoundary(_cursor);
                _text = _text.Remove(from, _cursor - from);
                _cursor = from;
                Changed?.Invoke();
                return true;
            case Key.Delete:
                if (_cursor >= _text.Length) return true;
                int to = ctrl ? WordRight(_cursor) : NextBoundary(_cursor);
                _text = _text.Remove(_cursor, to - _cursor);
                Changed?.Invoke();
                return true;
            case Key.Left:
                _cursor = ctrl ? WordLeft(_cursor) : PrevBoundary(_cursor);
                return true;
            case Key.Right:
                _cursor = ctrl ? WordRight(_cursor) : NextBoundary(_cursor);
                return true;
            case Key.Home:
                _cursor = 0;
                return true;
            case Key.End:
                _cursor = _text.Length;
                return true;
        }
        return false;
    }

    public void Render(ScreenBuffer s, int x, int y, int width, Style style, bool focused)
    {
        s.Fill(new Rect(x, y, width, 1), style);
        if (_text.Length == 0 && !focused || _text.Length == 0 && Placeholder.Length > 0)
        {
            s.TextTrunc(x, y, Placeholder, style with { Fg = Theme.Muted }, width);
            if (focused) s.Put(x, y, " ", style with { Attr = Attr.Reverse });
            return;
        }
        int cursorCol = TextUtil.Width(_text[.._cursor]);
        if (cursorCol - _scroll >= width) _scroll = cursorCol - width + 1;
        if (cursorCol < _scroll) _scroll = cursorCol;
        if (_scroll < 0) _scroll = 0;

        int col = 0;
        int idx = 0;
        foreach (var (g, gw) in TextUtil.Graphemes(_text))
        {
            int sx = col - _scroll;
            bool atCursor = focused && idx == _cursor;
            if (sx >= 0 && sx + gw <= width)
                s.Text(x + sx, y, g, atCursor ? style with { Attr = style.Attr | Attr.Reverse } : style);
            col += gw;
            idx += g.Length;
        }
        if (focused && _cursor >= _text.Length)
        {
            int sx = col - _scroll;
            if (sx >= 0 && sx < width) s.Put(x + sx, y, " ", style with { Attr = style.Attr | Attr.Reverse });
        }
    }
}
