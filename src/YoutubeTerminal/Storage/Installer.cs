using System.Reflection;
using System.Security.Cryptography;

namespace YoutubeTerminal.Storage;

/// <summary>
/// Pierwsze uruchomienie: wypakowuje wbudowany yt-dlp do folderu danych aplikacji (bin) oraz skrypt
/// deinstalatora obok pliku programu. Wbudowany ffmpeg (Linux/macOS) jest wypakowywany dopiero wtedy,
/// gdy jest potrzebny, a w systemie nie ma własnego.
/// </summary>
internal static class Installer
{
    private const string YtDlpResource = "yt-dlp";
    private const string FfmpegResource = "ffmpeg";
    private static readonly object ExtractLock = new();

    /// <summary>Nazwa deinstalatora — taka sama niezależnie od języka interfejsu i systemu.</summary>
    public static string UninstallerName => OperatingSystem.IsWindows() ? "uninstall.cmd" : "uninstall.sh";

    /// <summary>Nazwa deinstalatora w wersjach do 1.0 — przy aktualizacji usuwamy go, żeby nie zostały dwa.</summary>
    private static string LegacyUninstallerName => OperatingSystem.IsWindows() ? "odinstaluj.cmd" : "odinstaluj.sh";

    public static string ExeSuffix => OperatingSystem.IsWindows() ? ".exe" : "";

    public static string YtDlpPath => Path.Combine(AppPaths.BinDir, "yt-dlp" + ExeSuffix);

    public static string FfmpegPath => Path.Combine(AppPaths.BinDir, "ffmpeg" + ExeSuffix);

    public static bool HasEmbeddedYtDlp =>
        typeof(Installer).Assembly.GetManifestResourceInfo(YtDlpResource) != null;

    public static bool HasEmbeddedFfmpeg =>
        typeof(Installer).Assembly.GetManifestResourceInfo(FfmpegResource) != null;

    /// <summary>Wykonuje kroki instalacyjne. Zwraca listę komunikatów (np. ostrzeżeń) do pokazania użytkownikowi.</summary>
    public static List<string> Run()
    {
        var messages = new List<string>();
        AppPaths.EnsureCreated();
        CleanTemp();

        try { ExtractTool(YtDlpResource, YtDlpPath); }
        catch (Exception ex) { messages.Add(L.T("Nie udało się wypakować yt-dlp: ", "Could not extract yt-dlp: ") + ex.Message); }

        try { ExtractUninstaller(); }
        catch (Exception ex) { messages.Add(L.T("Nie udało się utworzyć deinstalatora: ", "Could not create the uninstaller: ") + ex.Message); }

        return messages;
    }

    /// <summary>Wypakowuje wbudowany ffmpeg (gdy program go zawiera). Zwraca ścieżkę albo null.</summary>
    public static string? ExtractFfmpeg() => ExtractTool(FfmpegResource, FfmpegPath) ? FfmpegPath : null;

    /// <summary>
    /// Wypakowuje zasób do pliku wykonywalnego — tylko gdy pliku brak albo program zawiera inną wersję niż
    /// poprzednio (dzięki temu np. „yt-dlp -U” wykonane przez użytkownika nie jest nadpisywane przy każdym starcie).
    /// </summary>
    private static bool ExtractTool(string resource, string target)
    {
        var asm = typeof(Installer).Assembly;
        using var res = asm.GetManifestResourceStream(resource);
        if (res == null) return File.Exists(target); // wersja bez wbudowanego narzędzia

        lock (ExtractLock)
        {
            string marker = target + ".embedded";
            string hash = ResourceFingerprint(asm, resource, res.Length);
            if (File.Exists(target) && File.Exists(marker) && File.ReadAllText(marker).Trim() == hash)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string tmp = target + ".new";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                res.CopyTo(fs);
            MakeExecutable(tmp);
            File.Move(tmp, target, overwrite: true);
            File.WriteAllText(marker, hash);
            return true;
        }
    }

    internal static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string ResourceFingerprint(Assembly asm, string resource, long length)
    {
        // Odcisk: długość + skrót pierwszych i ostatnich 64 KB (szybko, bez czytania całości).
        using var s = asm.GetManifestResourceStream(resource)!;
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
        byte[] content = WithExeName(ms.ToArray(), Path.GetFileName(AppPaths.ExePath));

        RemoveLegacyUninstaller(dir);
        RemoveLegacyUninstaller(AppPaths.Root);

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
            MakeExecutable(target);
        }
        catch (UnauthorizedAccessException)
        {
            // Folder programu tylko do odczytu (np. Program Files, /usr/local/bin) — deinstalator trafia do folderu danych.
            string fallback = Path.Combine(AppPaths.Root, UninstallerName);
            File.WriteAllBytes(fallback, content);
            MakeExecutable(fallback);
        }
    }

    /// <summary>
    /// Usuwa deinstalator o starej nazwie (odinstaluj.*) — tylko gdy to na pewno nasz skrypt.
    /// </summary>
    internal static void RemoveLegacyUninstaller(string dir)
    {
        try
        {
            string old = Path.Combine(dir, LegacyUninstallerName);
            if (!File.Exists(old) || new FileInfo(old).Length > 64 * 1024) return;
            if (IsOurUninstaller(File.ReadAllText(old))) File.Delete(old);
        }
        catch { /* zostawiamy — nie przeszkadza w działaniu */ }
    }

    internal static bool IsOurUninstaller(string text) =>
        text.Contains("YouTube Terminal", StringComparison.Ordinal) && text.Contains("YoutubeTerminal", StringComparison.Ordinal);

    /// <summary>
    /// Wstawia do skryptu deinstalatora faktyczną nazwę pliku programu (np. YoutubeTerminal-win-x64.exe
    /// z wydania na GitHubie), żeby usunął właściwy plik.
    /// </summary>
    internal static byte[] WithExeName(byte[] script, string exeName)
    {
        if (exeName.Length == 0 || exeName.IndexOfAny(['"', '%', '$', '`', '\\', '\n', '\r']) >= 0)
            exeName = AppInfo.ExeName;
        var text = System.Text.Encoding.UTF8.GetString(script);
        return System.Text.Encoding.UTF8.GetBytes(text.Replace("__EXE_NAME__", exeName));
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
