using System.Text;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Tui;

/// <summary>
/// Niskopoziomowa obsługa konsoli Windows w trybie VT: tryby wejścia/wyjścia, ekran alternatywny,
/// bezpośredni zapis bajtów (WriteFile) chroniony blokadą — współdzielony przez UI i wątek wideo.
/// </summary>
internal sealed unsafe class Terminal : ITerminalOutput, IDisposable
{
    private readonly IntPtr _in;
    private readonly IntPtr _out;
    private uint _origIn, _origOut, _origInCp, _origOutCp;
    private bool _initialized;
    private readonly object _writeLock = new();

    public int Width { get; private set; } = 80;
    public int Height { get; private set; } = 25;

    /// <summary>Rozmiar komórki w pikselach (dla Sixel).</summary>
    public int CellWidth { get; set; } = 10;
    public int CellHeight { get; set; } = 20;
    public bool CellSizeFromTerminal { get; set; }
    public bool SixelSupported { get; set; }
    public bool SyncOutputSupported { get; set; }
    public string TerminalName { get; set; } = "";

    public IntPtr InputHandle => _in;

    public Terminal()
    {
        _in = Win32Console.GetStdHandle(Win32Console.STD_INPUT_HANDLE);
        _out = Win32Console.GetStdHandle(Win32Console.STD_OUTPUT_HANDLE);
    }

    public void Init()
    {
        if (!Win32Console.GetConsoleMode(_out, out _origOut) || !Win32Console.GetConsoleMode(_in, out _origIn))
            throw new InvalidOperationException("Ten program musi być uruchomiony w konsoli (np. Windows Terminal).");

        _origInCp = Win32Console.GetConsoleCP();
        _origOutCp = Win32Console.GetConsoleOutputCP();
        Win32Console.SetConsoleOutputCP(65001);
        Win32Console.SetConsoleCP(65001);

        uint outMode = Win32Console.ENABLE_PROCESSED_OUTPUT | Win32Console.ENABLE_VIRTUAL_TERMINAL_PROCESSING | Win32Console.DISABLE_NEWLINE_AUTO_RETURN;
        if (!Win32Console.SetConsoleMode(_out, outMode))
            throw new InvalidOperationException("Konsola nie obsługuje sekwencji VT. Użyj Windows Terminal.");

        uint inMode = Win32Console.ENABLE_VIRTUAL_TERMINAL_INPUT | Win32Console.ENABLE_WINDOW_INPUT | Win32Console.ENABLE_EXTENDED_FLAGS;
        Win32Console.SetConsoleMode(_in, inMode);
        Win32Console.FlushConsoleInputBuffer(_in);
        _initialized = true;

        // Ekran alternatywny, ukryty kursor, bez zawijania, mysz SGR, wklejanie w nawiasach.
        WriteString("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[2J\x1b[H");
        UpdateSize();
    }

    public bool UpdateSize()
    {
        int w, h;
        try
        {
            w = Console.WindowWidth;
            h = Console.WindowHeight;
        }
        catch
        {
            return false;
        }
        if (w <= 0 || h <= 0) return false;
        if (w == Width && h == Height) return false;
        Width = w;
        Height = h;
        return true;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            WriteUnlocked(data);
        }
    }

    /// <summary>Zapis kilku fragmentów atomowo (bez przeplatania z innymi wątkami).</summary>
    public void WriteAtomic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default)
    {
        lock (_writeLock)
        {
            WriteUnlocked(a);
            WriteUnlocked(b);
            if (!c.IsEmpty) WriteUnlocked(c);
        }
    }

    private void WriteUnlocked(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                // Duże bloki dzielimy, żeby konsola nie odrzucała zapisów.
                uint chunk = (uint)Math.Min(data.Length - offset, 1 << 20);
                if (!Win32Console.WriteFile(_out, p + offset, chunk, out uint written, IntPtr.Zero) || written == 0)
                    return;
                offset += (int)written;
            }
        }
    }

    public void WriteString(string s) => Write(Encoding.UTF8.GetBytes(s));

    public void SetTitle(string title)
    {
        WriteString("\x1b]0;" + title.Replace('\x1b', ' ').Replace('\a', ' ') + "\a");
    }

    /// <summary>Kopiowanie do schowka przez OSC 52 (obsługiwane przez Windows Terminal).</summary>
    public void CopyToClipboard(string text)
    {
        WriteString("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\a");
    }

    public void Dispose()
    {
        if (!_initialized) return;
        _initialized = false;
        try
        {
            WriteString("\x1b[0m\x1b[2J\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l\x1b[?7h\x1b[?25h\x1b[?1049l");
        }
        catch { }
        Win32Console.SetConsoleMode(_out, _origOut);
        Win32Console.SetConsoleMode(_in, _origIn);
        if (_origOutCp != 0) Win32Console.SetConsoleOutputCP(_origOutCp);
        if (_origInCp != 0) Win32Console.SetConsoleCP(_origInCp);
    }
}
