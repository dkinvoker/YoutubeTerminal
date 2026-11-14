using System.Reflection;
using System.Security.Cryptography;

namespace YoutubeTerminal.Storage;

/// <summary>
/// Pierwsze uruchomienie: wypakowuje wbudowany yt-dlp.exe do %LOCALAPPDATA%\YoutubeTerminal\bin
/// oraz skrypt deinstalatora obok pliku .exe aplikacji.
/// </summary>
internal static class Installer
{
    public const string UninstallerName = "odinstaluj.cmd";
    private const string YtDlpResource = "yt-dlp.exe";

    public static string YtDlpPath => Path.Combine(AppPaths.BinDir, "yt-dlp.exe");

    public static bool HasEmbeddedYtDlp =>
        typeof(Installer).Assembly.GetManifestResourceInfo(YtDlpResource) != null;

    /// <summary>Wykonuje kroki instalacyjne. Zwraca listę komunikatów (np. ostrzeżeń) do pokazania użytkownikowi.</summary>
    public static List<string> Run()
    {
        var messages = new List<string>();
        AppPaths.EnsureCreated();
        CleanTemp();

        try { ExtractYtDlp(); }
        catch (Exception ex) { messages.Add("Nie udało się wypakować yt-dlp: " + ex.Message); }

        try { ExtractUninstaller(); }
        catch (Exception ex) { messages.Add("Nie udało się utworzyć deinstalatora: " + ex.Message); }

        return messages;
    }

    private static void ExtractYtDlp()
    {
        var asm = typeof(Installer).Assembly;
        using var res = asm.GetManifestResourceStream(YtDlpResource);
        if (res == null) return; // wersja bez wbudowanego yt-dlp

        string marker = Path.Combine(AppPaths.BinDir, "yt-dlp.embedded");
        string hash = ResourceFingerprint(asm, res.Length);

        // Wypakowujemy tylko, gdy pliku brak albo gdy aplikacja zawiera inną (nowszą) wersję niż poprzednio.
        // Dzięki temu "yt-dlp -U" wykonane przez użytkownika nie jest nadpisywane przy każdym starcie.
        if (File.Exists(YtDlpPath) && File.Exists(marker) && File.ReadAllText(marker).Trim() == hash)
            return;

        string tmp = YtDlpPath + ".new";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            res.CopyTo(fs);
        File.Move(tmp, YtDlpPath, overwrite: true);
        File.WriteAllText(marker, hash);
    }

    private static string ResourceFingerprint(Assembly asm, long length)
    {
        // Odcisk: długość + skrót pierwszych i ostatnich 64 KB (szybko, bez czytania całości).
        using var s = asm.GetManifestResourceStream(YtDlpResource)!;
        var buf = new byte[Math.Min(65536, length)];
        s.ReadExactly(buf);
        using var sha = SHA256.Create();
        sha.TransformBlock(buf, 0, buf.Length, null, 0);
        if (length > buf.Length)
        {
            s.Seek(-buf.Length, SeekOrigin.End);
            s.ReadExactly(buf);
        }
        sha.TransformFinalBlock(buf, 0, buf.Length);
        return length + ":" + Convert.ToHexString(sha.Hash!);
    }

    private static void ExtractUninstaller()
    {
        string dir = Path.GetDirectoryName(AppPaths.ExePath) ?? AppPaths.ExeDirectory;
        string target = Path.Combine(dir, UninstallerName);
        using var res = typeof(Installer).Assembly.GetManifestResourceStream(UninstallerName);
        if (res == null) return;
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        byte[] content = ms.ToArray();

        if (File.Exists(target))
        {
            try
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content)) return;
            }
            catch { /* nadpiszemy */ }
        }

        try
        {
            File.WriteAllBytes(target, content);
        }
        catch (UnauthorizedAccessException)
        {
            // Folder aplikacji tylko do odczytu (np. Program Files) — kładziemy deinstalator w folderze danych.
            File.WriteAllBytes(Path.Combine(AppPaths.Root, UninstallerName), content);
        }
    }

    /// <summary>Usuwa pozostałości po poprzednich uruchomieniach yt-dlp (foldery _MEI* w naszym tmp).</summary>
    private static void CleanTemp()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(AppPaths.TempDir))
            {
                try { Directory.Delete(d, true); } catch { /* w użyciu przez inną instancję */ }
            }
            foreach (var f in Directory.EnumerateFiles(AppPaths.TempDir))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
