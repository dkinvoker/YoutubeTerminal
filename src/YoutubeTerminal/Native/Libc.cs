using System.Runtime.InteropServices;

namespace YoutubeTerminal.Native;

/// <summary>
/// Kilka funkcji libc dla Linuksa i macOS. Nazwy bibliotek podajemy jawnie (libc.so.6 / libSystem),
/// bo „libc” nie jest wszędzie rozwiązywane tak samo. Używamy tylko funkcji o stałej liczbie
/// argumentów — wywołania funkcji wariadycznych (np. ioctl) przez P/Invoke są błędne na macOS arm64.
/// </summary>
internal static unsafe partial class Libc
{
    public const int EINTR = 4;
    public static int EAGAIN => OperatingSystem.IsMacOS() ? 35 : 11;
    public const short POLLIN = 0x1, POLLOUT = 0x4, POLLERR = 0x8, POLLHUP = 0x10;
    public const int TCSANOW = 0;

    /// <summary>Rozmiar bufora na struct termios (Linux: 60 B, macOS: 72 B) z zapasem.</summary>
    public const int TermiosSize = 256;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private static readonly bool Mac = OperatingSystem.IsMacOS();

    public static int IsATty(int fd) => Mac ? M.isatty(fd) : G.isatty(fd);
    public static int TcGetAttr(int fd, byte* termios) => Mac ? M.tcgetattr(fd, termios) : G.tcgetattr(fd, termios);
    public static int TcSetAttr(int fd, int actions, byte* termios) => Mac ? M.tcsetattr(fd, actions, termios) : G.tcsetattr(fd, actions, termios);
    public static void CfMakeRaw(byte* termios) { if (Mac) M.cfmakeraw(termios); else G.cfmakeraw(termios); }
    public static nint Read(int fd, byte* buf, nint count) => Mac ? M.read(fd, buf, count) : G.read(fd, buf, count);
    public static nint Write(int fd, byte* buf, nint count) => Mac ? M.write(fd, buf, count) : G.write(fd, buf, count);
    public static int Poll(PollFd* fds, nuint count, int timeoutMs) => Mac ? M.poll(fds, count, timeoutMs) : G.poll(fds, count, timeoutMs);

    /// <summary>glibc (Linux).</summary>
    private static partial class G
    {
        private const string Lib = "libc.so.6";
        [LibraryImport(Lib)] public static partial int isatty(int fd);
        [LibraryImport(Lib, SetLastError = true)] public static partial int tcgetattr(int fd, byte* termios);
        [LibraryImport(Lib, SetLastError = true)] public static partial int tcsetattr(int fd, int actions, byte* termios);
        [LibraryImport(Lib)] public static partial void cfmakeraw(byte* termios);
        [LibraryImport(Lib, SetLastError = true)] public static partial nint read(int fd, byte* buf, nint count);
        [LibraryImport(Lib, SetLastError = true)] public static partial nint write(int fd, byte* buf, nint count);
        [LibraryImport(Lib, SetLastError = true)] public static partial int poll(PollFd* fds, nuint count, int timeoutMs);
    }

    /// <summary>libSystem (macOS).</summary>
    private static partial class M
    {
        private const string Lib = "/usr/lib/libSystem.B.dylib";
        [LibraryImport(Lib)] public static partial int isatty(int fd);
        [LibraryImport(Lib, SetLastError = true)] public static partial int tcgetattr(int fd, byte* termios);
        [LibraryImport(Lib, SetLastError = true)] public static partial int tcsetattr(int fd, int actions, byte* termios);
        [LibraryImport(Lib)] public static partial void cfmakeraw(byte* termios);
        [LibraryImport(Lib, SetLastError = true)] public static partial nint read(int fd, byte* buf, nint count);
        [LibraryImport(Lib, SetLastError = true)] public static partial nint write(int fd, byte* buf, nint count);
        [LibraryImport(Lib, SetLastError = true)] public static partial int poll(PollFd* fds, nuint count, int timeoutMs);
    }
}
