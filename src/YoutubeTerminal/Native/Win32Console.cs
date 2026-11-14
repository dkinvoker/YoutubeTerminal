using System.Runtime.InteropServices;

namespace YoutubeTerminal.Native;

internal static unsafe partial class Win32Console
{
    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;

    // Tryby wejścia
    public const uint ENABLE_PROCESSED_INPUT = 0x0001;
    public const uint ENABLE_LINE_INPUT = 0x0002;
    public const uint ENABLE_ECHO_INPUT = 0x0004;
    public const uint ENABLE_WINDOW_INPUT = 0x0008;
    public const uint ENABLE_MOUSE_INPUT = 0x0010;
    public const uint ENABLE_INSERT_MODE = 0x0020;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    // Tryby wyjścia
    public const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    public const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    public const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    public const ushort KEY_EVENT = 0x0001;
    public const ushort MOUSE_EVENT = 0x0002;
    public const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
    public const ushort FOCUS_EVENT = 0x0010;

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        // KEY_EVENT_RECORD
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public char UnicodeChar;
        [FieldOffset(16)] public uint ControlKeyState;
        // WINDOW_BUFFER_SIZE_RECORD
        [FieldOffset(4)] public short SizeX;
        [FieldOffset(6)] public short SizeY;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleMode(IntPtr handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleMode(IntPtr handle, uint mode);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadConsoleInput(IntPtr handle, INPUT_RECORD* buffer, uint length, out uint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(IntPtr handle, byte* buffer, uint count, out uint written, IntPtr overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleOutputCP(uint codePage);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleCP(uint codePage);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushConsoleInputBuffer(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleTitleW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetConsoleTitle(string title);
}
