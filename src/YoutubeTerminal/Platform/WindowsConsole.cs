using System.Text;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Platform;

/// <summary>Konsola Windows w trybie VT (wejście i wyjście), zapis przez WriteFile.</summary>
internal sealed unsafe class WindowsConsole : IConsoleBackend
{
    private readonly IntPtr _in;
    private readonly IntPtr _out;
    private uint _origIn, _origOut, _origInCp, _origOutCp;
    private bool _initialized;
    private readonly Win32Console.INPUT_RECORD[] _records = new Win32Console.INPUT_RECORD[128];

    public WindowsConsole()
    {
        _in = Win32Console.GetStdHandle(Win32Console.STD_INPUT_HANDLE);
        _out = Win32Console.GetStdHandle(Win32Console.STD_OUTPUT_HANDLE);
    }

    public void Init()
    {
        if (!Win32Console.GetConsoleMode(_out, out _origOut) || !Win32Console.GetConsoleMode(_in, out _origIn))
            throw new InvalidOperationException(L.T("Ten program musi być uruchomiony w terminalu (np. Windows Terminal).", "This program must run in a terminal (e.g. Windows Terminal)."));

        _origInCp = Win32Console.GetConsoleCP();
        _origOutCp = Win32Console.GetConsoleOutputCP();
        Win32Console.SetConsoleOutputCP(65001);
        Win32Console.SetConsoleCP(65001);

        uint outMode = Win32Console.ENABLE_PROCESSED_OUTPUT | Win32Console.ENABLE_VIRTUAL_TERMINAL_PROCESSING | Win32Console.DISABLE_NEWLINE_AUTO_RETURN;
        if (!Win32Console.SetConsoleMode(_out, outMode))
            throw new InvalidOperationException(L.T("Konsola nie obsługuje sekwencji VT. Użyj Windows Terminal.", "The console does not support VT sequences. Use Windows Terminal."));

        uint inMode = Win32Console.ENABLE_VIRTUAL_TERMINAL_INPUT | Win32Console.ENABLE_WINDOW_INPUT | Win32Console.ENABLE_EXTENDED_FLAGS;
        Win32Console.SetConsoleMode(_in, inMode);
        Win32Console.FlushConsoleInputBuffer(_in);
        _initialized = true;
    }

    public void Restore()
    {
        if (!_initialized) return;
        _initialized = false;
        Win32Console.SetConsoleMode(_out, _origOut);
        Win32Console.SetConsoleMode(_in, _origIn);
        if (_origOutCp != 0) Win32Console.SetConsoleOutputCP(_origOutCp);
        if (_origInCp != 0) Win32Console.SetConsoleCP(_origInCp);
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
                // Duże bloki dzielimy, żeby konsola nie odrzucała zapisów.
                uint chunk = (uint)Math.Min(data.Length - offset, 1 << 20);
                if (!Win32Console.WriteFile(_out, p + offset, chunk, out uint written, IntPtr.Zero) || written == 0)
                    return;
                offset += (int)written;
            }
        }
    }

    public bool Read(StringBuilder into, int timeoutMs, out bool resized)
    {
        resized = false;
        uint wait = Win32Console.WaitForSingleObject(_in, (uint)Math.Max(0, timeoutMs));
        if (wait != Win32Console.WAIT_OBJECT_0) return false;

        uint read;
        fixed (Win32Console.INPUT_RECORD* p = _records)
        {
            if (!Win32Console.ReadConsoleInput(_in, p, (uint)_records.Length, out read)) { Thread.Sleep(10); return false; }
        }
        for (int i = 0; i < read; i++)
        {
            ref var r = ref _records[i];
            if (r.EventType == Win32Console.KEY_EVENT)
            {
                if (r.KeyDown == 0 || r.UnicodeChar == '\0') continue;
                int rep = Math.Max(1, (int)r.RepeatCount);
                for (int k = 0; k < rep; k++) into.Append(r.UnicodeChar);
            }
            else if (r.EventType == Win32Console.WINDOW_BUFFER_SIZE_EVENT)
            {
                resized = true;
            }
        }
        return true;
    }

    public void Dispose() => Restore();
}
