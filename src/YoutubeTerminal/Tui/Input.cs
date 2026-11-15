using System.Collections.Concurrent;
using System.Text;

namespace YoutubeTerminal.Tui;

internal enum Key
{
    None, Char, Enter, Escape, Backspace, Tab, BackTab, Up, Down, Left, Right,
    Home, End, PageUp, PageDown, Insert, Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

[Flags]
internal enum Mods { None = 0, Shift = 1, Alt = 2, Ctrl = 4 }

internal abstract class InputEvent;

internal sealed class KeyEvent : InputEvent
{
    public Key Key;
    public char Char;
    public Mods Mods;

    public KeyEvent(Key key, char ch = '\0', Mods mods = Mods.None) { Key = key; Char = ch; Mods = mods; }

    public bool Is(char c) => Key == Key.Char && Char == c && (Mods & (Mods.Ctrl | Mods.Alt)) == 0;
    public bool IsCtrl(char c) => Key == Key.Char && (Mods & Mods.Ctrl) != 0 && char.ToLowerInvariant(Char) == c;
    public override string ToString() => Key == Key.Char ? $"{Mods}+'{Char}'" : $"{Mods}+{Key}";
}

internal enum MouseKind { Press, Release, Move, WheelUp, WheelDown }

internal sealed class MouseEvent : InputEvent
{
    public MouseKind Kind;
    public int Button; // 0 lewy, 1 środkowy, 2 prawy
    public int X, Y;   // 0-based komórki
    public Mods Mods;
}

internal sealed class PasteEvent : InputEvent
{
    public string Text = "";
}

internal sealed class ResizeEvent : InputEvent;

/// <summary>Odpowiedź terminala na zapytanie (np. DA1, rozmiar komórki).</summary>
internal sealed class ReplyEvent : InputEvent
{
    public char Prefix;   // np. '?' dla DA1
    public char Final;    // np. 'c' lub 't'
    public int[] Params = [];
    public string Raw = "";
}

/// <summary>Parser sekwencji VT napływających z konsoli w trybie ENABLE_VIRTUAL_TERMINAL_INPUT.</summary>
internal sealed class VtParser
{
    private readonly List<InputEvent> _out = new();
    private readonly StringBuilder _paste = new();
    private bool _inPaste;

    /// <summary>
    /// Przetwarza bufor znaków. Zwraca liczbę zużytych znaków — niekompletna sekwencja na końcu
    /// zostaje w buforze, chyba że <paramref name="final"/> = true (wtedy np. samotny ESC to klawisz Escape).
    /// </summary>
    public int Parse(ReadOnlySpan<char> s, bool final, List<InputEvent> output)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (_inPaste)
            {
                int end = s[i..].IndexOf("\x1b[201~".AsSpan());
                if (end < 0)
                {
                    // Zostawiamy ewentualny początek terminatora na kolejną porcję.
                    int keep = Math.Min(5, s.Length - i);
                    int take = final ? s.Length - i : s.Length - i - keep;
                    if (take > 0) { _paste.Append(s.Slice(i, take)); i += take; }
                    return i;
                }
                _paste.Append(s.Slice(i, end));
                i += end + 6;
                _inPaste = false;
                output.Add(new PasteEvent { Text = _paste.ToString() });
                _paste.Clear();
                continue;
            }

            char c = s[i];
            if (c == '\x1b')
            {
                if (i + 1 >= s.Length)
                {
                    if (!final) return i;
                    output.Add(new KeyEvent(Key.Escape));
                    i++;
                    continue;
                }
                char n = s[i + 1];
                if (n == '[')
                {
                    int consumed = ParseCsi(s[(i + 2)..], final, output);
                    if (consumed < 0) { if (!final) return i; output.Add(new KeyEvent(Key.Char, '[', Mods.Alt)); i += 2; continue; }
                    i += 2 + consumed;
                    continue;
                }
                if (n == 'O')
                {
                    if (i + 2 >= s.Length) { if (!final) return i; output.Add(new KeyEvent(Key.Char, 'O', Mods.Alt)); i += 2; continue; }
                    output.Add(Ss3(s[i + 2]));
                    i += 3;
                    continue;
                }
                if (n == ']' || n == 'P' || n == '_' || n == '^')
                {
                    // OSC/DCS/APC/PM — pomijamy do ST (ESC \) lub BEL
                    int j = i + 2;
                    bool done = false;
                    for (; j < s.Length; j++)
                    {
                        if (s[j] == '\a') { j++; done = true; break; }
                        if (s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\') { j += 2; done = true; break; }
                    }
                    if (!done && !final) return i;
                    i = j;
                    continue;
                }
                if (n == '\x1b')
                {
                    output.Add(new KeyEvent(Key.Escape));
                    i++;
                    continue;
                }
                // Alt + znak
                var inner = ControlChar(n);
                inner.Mods |= Mods.Alt;
                output.Add(inner);
                i += 2;
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length) { if (!final) return i; i++; continue; }
                // Znak spoza BMP — przekazujemy jako dwa zdarzenia znakowe (pole tekstowe sklei je w łańcuch)
                output.Add(new KeyEvent(Key.Char, c));
                output.Add(new KeyEvent(Key.Char, s[i + 1]));
                i += 2;
                continue;
            }

            output.Add(ControlChar(c));
            i++;
        }
        return i;
    }

    private static KeyEvent ControlChar(char c) => c switch
    {
        '\r' or '\n' => new KeyEvent(Key.Enter),
        '\t' => new KeyEvent(Key.Tab),
        '\x7f' => new KeyEvent(Key.Backspace),
        '\b' => new KeyEvent(Key.Backspace, '\0', Mods.Ctrl),
        '\0' => new KeyEvent(Key.Char, ' ', Mods.Ctrl),
        < ' ' => new KeyEvent(Key.Char, (char)('a' + c - 1), Mods.Ctrl),
        _ => new KeyEvent(Key.Char, c),
    };

    private static KeyEvent Ss3(char f) => f switch
    {
        'P' => new KeyEvent(Key.F1),
        'Q' => new KeyEvent(Key.F2),
        'R' => new KeyEvent(Key.F3),
        'S' => new KeyEvent(Key.F4),
        'A' => new KeyEvent(Key.Up),
        'B' => new KeyEvent(Key.Down),
        'C' => new KeyEvent(Key.Right),
        'D' => new KeyEvent(Key.Left),
        'H' => new KeyEvent(Key.Home),
        'F' => new KeyEvent(Key.End),
        'M' => new KeyEvent(Key.Enter),
        _ => new KeyEvent(Key.None),
    };

    /// <summary>Parsuje treść CSI (po "ESC ["). Zwraca liczbę znaków lub -1 gdy niekompletne.</summary>
    private int ParseCsi(ReadOnlySpan<char> s, bool final, List<InputEvent> output)
    {
        int j = 0;
        char prefix = '\0';
        if (j < s.Length && (s[j] is '<' or '?' or '>' or '=')) prefix = s[j++];
        int paramStart = j;
        while (j < s.Length && (char.IsAsciiDigit(s[j]) || s[j] == ';' || s[j] == ':')) j++;
        while (j < s.Length && s[j] >= 0x20 && s[j] <= 0x2F) j++; // znaki pośrednie
        if (j >= s.Length) return final ? s.Length : -1;
        char fin = s[j];
        if (fin < 0x40 || fin > 0x7E) return j; // śmieci — pomijamy
        var paramText = s[paramStart..j];
        var ps = ParseParams(paramText);
        int consumed = j + 1;

        if (prefix == '<' && (fin == 'M' || fin == 'm'))
        {
            output.Add(Mouse(ps, fin == 'm'));
            return consumed;
        }
        if (prefix == '?' || fin == 't' || (prefix == '>' && fin == 'c'))
        {
            output.Add(new ReplyEvent { Prefix = prefix, Final = fin, Params = ps, Raw = new string(paramText) });
            return consumed;
        }

        Mods mods = ps.Length >= 2 ? DecodeMods(ps[1]) : Mods.None;
        KeyEvent? key = fin switch
        {
            'A' => new KeyEvent(Key.Up, '\0', mods),
            'B' => new KeyEvent(Key.Down, '\0', mods),
            'C' => new KeyEvent(Key.Right, '\0', mods),
            'D' => new KeyEvent(Key.Left, '\0', mods),
            'H' => new KeyEvent(Key.Home, '\0', mods),
            'F' => new KeyEvent(Key.End, '\0', mods),
            'Z' => new KeyEvent(Key.BackTab, '\0', Mods.Shift),
            'P' => new KeyEvent(Key.F1, '\0', mods),
            'Q' => new KeyEvent(Key.F2, '\0', mods),
            'R' when ps.Length < 2 || ps[0] == 1 => new KeyEvent(Key.F3, '\0', mods),
            'S' => new KeyEvent(Key.F4, '\0', mods),
            '~' => Tilde(ps.Length > 0 ? ps[0] : 0, mods),
            _ => null,
        };

        if (fin == '~' && ps.Length > 0 && ps[0] == 200)
        {
            _inPaste = true;
            _paste.Clear();
            return consumed;
        }
        if (fin == 'R' && ps.Length >= 2 && ps[0] != 1)
        {
            output.Add(new ReplyEvent { Prefix = prefix, Final = fin, Params = ps, Raw = new string(paramText) });
            return consumed;
        }
        if (key != null && key.Key != Key.None) output.Add(key);
        return consumed;
    }

    private static KeyEvent Tilde(int code, Mods mods) => code switch
    {
        1 or 7 => new KeyEvent(Key.Home, '\0', mods),
        2 => new KeyEvent(Key.Insert, '\0', mods),
        3 => new KeyEvent(Key.Delete, '\0', mods),
        4 or 8 => new KeyEvent(Key.End, '\0', mods),
        5 => new KeyEvent(Key.PageUp, '\0', mods),
        6 => new KeyEvent(Key.PageDown, '\0', mods),
        11 => new KeyEvent(Key.F1, '\0', mods),
        12 => new KeyEvent(Key.F2, '\0', mods),
        13 => new KeyEvent(Key.F3, '\0', mods),
        14 => new KeyEvent(Key.F4, '\0', mods),
        15 => new KeyEvent(Key.F5, '\0', mods),
        17 => new KeyEvent(Key.F6, '\0', mods),
        18 => new KeyEvent(Key.F7, '\0', mods),
        19 => new KeyEvent(Key.F8, '\0', mods),
        20 => new KeyEvent(Key.F9, '\0', mods),
        21 => new KeyEvent(Key.F10, '\0', mods),
        23 => new KeyEvent(Key.F11, '\0', mods),
        24 => new KeyEvent(Key.F12, '\0', mods),
        _ => new KeyEvent(Key.None),
    };

    private static Mods DecodeMods(int v)
    {
        v -= 1;
        Mods m = Mods.None;
        if ((v & 1) != 0) m |= Mods.Shift;
        if ((v & 2) != 0) m |= Mods.Alt;
        if ((v & 4) != 0) m |= Mods.Ctrl;
        return m;
    }

    private static MouseEvent Mouse(int[] ps, bool release)
    {
        int b = ps.Length > 0 ? ps[0] : 0;
        int x = ps.Length > 1 ? ps[1] - 1 : 0;
        int y = ps.Length > 2 ? ps[2] - 1 : 0;
        var ev = new MouseEvent { X = x, Y = y, Button = b & 3 };
        if ((b & 4) != 0) ev.Mods |= Mods.Shift;
        if ((b & 8) != 0) ev.Mods |= Mods.Alt;
        if ((b & 16) != 0) ev.Mods |= Mods.Ctrl;
        if ((b & 64) != 0) ev.Kind = (b & 1) != 0 ? MouseKind.WheelDown : MouseKind.WheelUp;
        else if ((b & 32) != 0) ev.Kind = MouseKind.Move;
        else ev.Kind = release ? MouseKind.Release : MouseKind.Press;
        return ev;
    }

    private static int[] ParseParams(ReadOnlySpan<char> p)
    {
        if (p.IsEmpty) return [];
        var list = new List<int>(4);
        int cur = 0;
        bool any = false;
        foreach (char c in p)
        {
            if (char.IsAsciiDigit(c)) { cur = cur * 10 + (c - '0'); any = true; }
            else if (c == ';' || c == ':') { list.Add(any ? cur : 0); cur = 0; any = false; }
        }
        list.Add(any ? cur : 0);
        return list.ToArray();
    }
}

/// <summary>Wątek czytający zdarzenia konsoli i zamieniający je na zdarzenia aplikacji.</summary>
internal sealed class InputReader : IDisposable
{
    private readonly Terminal _term;
    private readonly BlockingCollection<InputEvent> _queue;
    private readonly Action _wake;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly VtParser _parser = new();

    public InputReader(Terminal term, BlockingCollection<InputEvent> queue, Action wake)
    {
        _term = term;
        _queue = queue;
        _wake = wake;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        var pending = new StringBuilder();
        var events = new List<InputEvent>();
        var backend = _term.Backend;

        while (!_stop)
        {
            bool timedOut = !backend.Read(pending, pending.Length > 0 ? 40 : 200, out bool resized);
            if (resized) _queue.Add(new ResizeEvent());

            if (pending.Length == 0)
            {
                if (!timedOut) _wake();
                continue;
            }

            events.Clear();
            var chars = pending.ToString();
            int consumed = _parser.Parse(chars, final: timedOut, events);
            pending.Remove(0, consumed);
            foreach (var e in events) _queue.Add(e);
            if (events.Count > 0) _wake();
        }
    }

    public void Dispose()
    {
        _stop = true;
    }
}
