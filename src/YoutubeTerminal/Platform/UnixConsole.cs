using System.Runtime.InteropServices;
using System.Text;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Platform;

/// <summary>
/// Terminal w Linuksie i macOS: tryb surowy (cfmakeraw), odczyt przez poll/read, zapis przez write.
/// Rozmiar okna podaje .NET (Console.WindowWidth/Height — śledzi SIGWINCH).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed unsafe class UnixConsole : IConsoleBackend
{
    private const int StdIn = 0, StdOut = 1;
    private byte* _orig;
    private bool _raw;
    private bool _eof;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly byte[] _bytes = new byte[4096];
    private readonly char[] _chars = new char[4096 + 8];
    private PosixSignalRegistration? _winch;
    private volatile bool _resized;

    public void Init()
    {
        if (Libc.IsATty(StdIn) == 0 || Libc.IsATty(StdOut) == 0)
            throw new InvalidOperationException(L.T("Ten program musi być uruchomiony w terminalu.", "This program must run in a terminal."));

        // .NET przygotowuje obsługę terminala przy pierwszym użyciu Console i przy wyjściu przywraca
        // zapamiętane wtedy ustawienia — musi je zapamiętać PRZED przejściem w tryb surowy.
        TryGetSize(out _, out _);

        _orig = (byte*)NativeMemory.AllocZeroed(Libc.TermiosSize);
        if (Libc.TcGetAttr(StdIn, _orig) != 0)
            throw new InvalidOperationException(L.T("Nie można odczytać ustawień terminala.", "Cannot read the terminal settings."));
        byte* raw = stackalloc byte[Libc.TermiosSize];
        new ReadOnlySpan<byte>(_orig, Libc.TermiosSize).CopyTo(new Span<byte>(raw, Libc.TermiosSize));
        // Tryb surowy: bez echa, bez buforowania linii, bez sygnałów z klawiatury (Ctrl+C przychodzi jako znak).
        Libc.CfMakeRaw(raw);
        if (Libc.TcSetAttr(StdIn, Libc.TCSANOW, raw) != 0)
            throw new InvalidOperationException(L.T("Nie można przełączyć terminala w tryb surowy.", "Cannot switch the terminal to raw mode."));
        _raw = true;

        try { _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => _resized = true); }
        catch { /* rozmiar i tak sprawdzamy w pętli */ }
    }

    public void Restore()
    {
        if (!_raw) return;
        _raw = false;
        Libc.TcSetAttr(StdIn, Libc.TCSANOW, _orig);
        _winch?.Dispose();
        _winch = null;
    }

    public bool TryGetSize(out int columns, out int rows)
    {
        try
        {
            columns = Console.WindowWidth;
            rows = Console.WindowHeight;
            return columns > 0 && rows > 0;
        }
        catch
        {
            columns = rows = 0;
            return false;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                nint n = Libc.Write(StdOut, p + offset, data.Length - offset);
                if (n < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == Libc.EINTR) continue;
                    if (err == Libc.EAGAIN)
                    {
                        var fd = new Libc.PollFd { Fd = StdOut, Events = Libc.POLLOUT };
                        Libc.Poll(&fd, 1, 100);
                        continue;
                    }
                    return;
                }
                offset += (int)n;
            }
        }
    }

    public bool Read(StringBuilder into, int timeoutMs, out bool resized)
    {
        resized = false;
        if (_resized) { _resized = false; resized = true; }
        if (_eof) { Thread.Sleep(Math.Max(1, timeoutMs)); return resized; }

        var fd = new Libc.PollFd { Fd = StdIn, Events = Libc.POLLIN };
        int r = Libc.Poll(&fd, 1, resized ? 0 : Math.Max(0, timeoutMs));
        if (r <= 0 || (fd.Revents & (Libc.POLLIN | Libc.POLLHUP | Libc.POLLERR)) == 0) return resized;

        nint n;
        fixed (byte* b = _bytes) n = Libc.Read(StdIn, b, _bytes.Length);
        if (n < 0) return resized;
        if (n == 0)
        {
            // Terminal zamknięty — kończymy program tak, jak po Ctrl+C.
            _eof = true;
            into.Append('\x03');
            return true;
        }
        int count = _decoder.GetChars(_bytes, 0, (int)n, _chars, 0, flush: false);
        into.Append(_chars, 0, count);
        return true;
    }

    public void Dispose()
    {
        Restore();
        if (_orig != null)
        {
            NativeMemory.Free(_orig);
            _orig = null;
        }
    }
}
