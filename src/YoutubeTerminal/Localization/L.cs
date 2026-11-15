using System.Runtime.InteropServices;

namespace YoutubeTerminal;

internal enum Lang { En, Pl }

/// <summary>
/// Język interfejsu. Teksty są zapisane parami przy miejscu użycia: <c>L.T("polski", "english")</c>.
/// Przy dwóch językach to najprostsze i zgodne z AOT (bez plików zasobów i refleksji).
/// </summary>
internal static class L
{
    public static Lang Current { get; set; } = Lang.En;

    public static bool IsPolish => Current == Lang.Pl;

    /// <summary>Kod języka (do parametru hl YouTube i nazw w ustawieniach).</summary>
    public static string Code => Current == Lang.Pl ? "pl" : "en";

    public static string T(string pl, string en) => Current == Lang.Pl ? pl : en;

    /// <summary>Ustawienie „auto” | „pl” | „en” → język.</summary>
    public static Lang Resolve(string? setting) => setting?.ToLowerInvariant() switch
    {
        "pl" => Lang.Pl,
        "en" => Lang.En,
        _ => Detect(),
    };

    /// <summary>Język systemu: zmienne środowiskowe (Linux/macOS), a w Windows język interfejsu użytkownika.</summary>
    public static Lang Detect()
    {
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG", "LANGUAGE" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v) || v is "C" or "POSIX" || v.StartsWith("C.", StringComparison.Ordinal)) continue;
            return FromTag(v);
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Język interfejsu Windows (np. 0x0415 = polski); działa także przy InvariantGlobalization.
                return (GetUserDefaultUILanguage() & 0x3FF) == 0x15 ? Lang.Pl : Lang.En;
            }
            catch { }
        }
        if (OperatingSystem.IsMacOS() && MacPreferredLanguage() is { } mac) return FromTag(mac);
        return Lang.En;
    }

    /// <summary>Region systemu (dwuliterowy kod kraju) albo null, gdy nie da się go ustalić.</summary>
    public static string? DetectRegion()
    {
        foreach (var name in new[] { "LC_ALL", "LANG" })
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) continue;
            int sep = v.IndexOfAny(['_', '-']);
            if (sep > 0 && v.Length >= sep + 3) return v.Substring(sep + 1, 2).ToUpperInvariant();
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string tag;
                unsafe
                {
                    char* buf = stackalloc char[85];
                    int n = GetUserDefaultLocaleName(buf, 85);
                    tag = n > 1 ? new string(buf, 0, n - 1) : "";
                }
                int sep = tag.LastIndexOf('-');
                if (sep > 0 && tag.Length - sep - 1 == 2) return tag[(sep + 1)..].ToUpperInvariant();
            }
            catch { }
        }
        return null;
    }

    internal static Lang FromTag(string tag) =>
        tag.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? Lang.Pl : Lang.En;

    private static string? MacPreferredLanguage()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("defaults", "read -g AppleLanguages")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            // Wynik: ( "pl-PL", "en-US" ) — bierzemy pierwszy wpis.
            int q = output.IndexOf('"');
            if (q >= 0)
            {
                int q2 = output.IndexOf('"', q + 1);
                if (q2 > q) return output[(q + 1)..q2];
            }
            var first = output.Trim('(', ')', ' ', '\n', '\r').Split(',', '\n')[0].Trim();
            return first.Length > 0 ? first : null;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll")]
    private static extern unsafe int GetUserDefaultLocaleName(char* buffer, int length);
}
