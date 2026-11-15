using System.Text;

namespace YoutubeTerminal.Platform;

/// <summary>
/// Systemowa część obsługi terminala: tryb surowy, zapis bajtów, odczyt wejścia jako znaków.
/// Wszystko powyżej (sekwencje VT, parser klawiszy, renderer) jest wspólne dla systemów.
/// </summary>
internal interface IConsoleBackend : IDisposable
{
    /// <summary>Włącza tryb surowy i sekwencje VT. Wyjątek, gdy program nie działa w terminalu.</summary>
    void Init();

    /// <summary>Przywraca tryby terminala sprzed <see cref="Init"/>.</summary>
    void Restore();

    bool TryGetSize(out int columns, out int rows);

    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Czeka na wejście najwyżej <paramref name="timeoutMs"/> ms i dopisuje odczytane znaki do <paramref name="into"/>.
    /// Zwraca false po upływie czasu bez żadnego zdarzenia.
    /// </summary>
    bool Read(StringBuilder into, int timeoutMs, out bool resized);
}

internal static class ConsoleBackend
{
    public static IConsoleBackend Create() =>
        OperatingSystem.IsWindows() ? new WindowsConsole() : new UnixConsole();
}
