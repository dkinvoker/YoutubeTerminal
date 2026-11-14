namespace YoutubeTerminal.Storage;

/// <summary>
/// Wszystkie pliki tworzone przez aplikację trafiają do jednego folderu
/// (%LOCALAPPDATA%\YoutubeTerminal), dzięki czemu deinstalator może łatwo posprzątać.
/// </summary>
internal static class AppPaths
{
    public const string AppFolderName = "YoutubeTerminal";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        AppFolderName);

    /// <summary>Wypakowany yt-dlp.exe.</summary>
    public static string BinDir => Path.Combine(Root, "bin");

    /// <summary>Cache yt-dlp (zamiast domyślnego %USERPROFILE%\.cache\yt-dlp).</summary>
    public static string YtDlpCacheDir => Path.Combine(Root, "cache", "yt-dlp");

    /// <summary>Folder tymczasowy dla yt-dlp (PyInstaller rozpakowuje się tutaj, nie do %TEMP%).</summary>
    public static string TempDir => Path.Combine(Root, "tmp");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string LogFile => Path.Combine(Root, "log.txt");

    public static string ExeDirectory => AppContext.BaseDirectory;
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "YoutubeTerminal.exe");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BinDir);
        Directory.CreateDirectory(YtDlpCacheDir);
        Directory.CreateDirectory(TempDir);
    }
}
