namespace YoutubeTerminal.Storage;

/// <summary>Prosty dziennik błędów w %LOCALAPPDATA%\YoutubeTerminal\log.txt (maks. ~1 MB).</summary>
internal static class Log
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var path = AppPaths.LogFile;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logowanie nie może przerwać programu
        }
    }

    public static void Error(string context, Exception ex) => Write($"{context}: {ex}");

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Nieobsłużony wyjątek: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Nieobserwowany wyjątek zadania: " + e.Exception);
            e.SetObserved();
        };
    }
}
