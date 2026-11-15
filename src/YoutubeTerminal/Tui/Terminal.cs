using System.Text;
using YoutubeTerminal.Platform;

namespace YoutubeTerminal.Tui;

/// <summary>
/// Terminal w trybie VT: ekran alternatywny, mysz, wklejanie w nawiasach i bezpośredni zapis bajtów
/// chroniony blokadą (współdzielony przez UI i wątek wideo). Część zależna od systemu jest w <see cref="IConsoleBackend"/>.
/// </summary>
internal sealed class Terminal : ITerminalOutput, IDisposable
{
    private readonly IConsoleBackend _backend;
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

    public IConsoleBackend Backend => _backend;

    public Terminal() : this(ConsoleBackend.Create()) { }

    public Terminal(IConsoleBackend backend) => _backend = backend;

    public void Init()
    {
        _backend.Init();
        _initialized = true;
        // Sprzątanie także przy zakończeniu przez SIGTERM (Linux/macOS) lub zamknięciu okna.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();

        // .NET w Unix może przełączyć klawiaturę numeryczną i kursory w tryb aplikacji — wracamy do zwykłego.
        if (!OperatingSystem.IsWindows()) WriteString("\x1b[?1l\x1b>");
        // Ekran alternatywny, ukryty kursor, bez zawijania, mysz SGR, wklejanie w nawiasach.
        WriteString("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[?1000h\x1b[?1002h\x1b[?1006h\x1b[?2004h\x1b[2J\x1b[H");
        UpdateSize();
    }

    public bool UpdateSize()
    {
        if (!_backend.TryGetSize(out int w, out int h)) return false;
        if (w == Width && h == Height) return false;
        Width = w;
        Height = h;
        return true;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            _backend.Write(data);
        }
    }

    /// <summary>Zapis kilku fragmentów atomowo (bez przeplatania z innymi wątkami).</summary>
    public void WriteAtomic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default)
    {
        lock (_writeLock)
        {
            _backend.Write(a);
            _backend.Write(b);
            if (!c.IsEmpty) _backend.Write(c);
        }
    }

    public void WriteString(string s) => Write(Encoding.UTF8.GetBytes(s));

    public void SetTitle(string title)
    {
        WriteString("\x1b]0;" + title.Replace('\x1b', ' ').Replace('\a', ' ') + "\a");
    }

    /// <summary>Kopiowanie do schowka przez OSC 52 (Windows Terminal, iTerm2, kitty, WezTerm, foot…).</summary>
    public void CopyToClipboard(string text)
    {
        WriteString("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\a");
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (!_initialized) return;
            _initialized = false;
            try
            {
                _backend.Write(Encoding.ASCII.GetBytes("\x1b[0m\x1b[2J\x1b[?2004l\x1b[?1006l\x1b[?1002l\x1b[?1000l\x1b[?7h\x1b[?25h\x1b[?1049l"));
            }
            catch { }
            _backend.Restore();
        }
    }
}
