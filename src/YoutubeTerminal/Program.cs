using System.Text;
using YoutubeTerminal;
using YoutubeTerminal.Storage;
using YoutubeTerminal.UI;

// Język interfejsu jest potrzebny już przy pierwszych komunikatach (pomoc, instalacja).
L.Current = L.Resolve(Settings.Load().UiLanguage);

if (args.Length > 0 && args[0].StartsWith("--diag", StringComparison.Ordinal))
    return await Diagnostics.RunAsync(args);

if (args.Length > 0 && (args[0] is "-v" or "--version"))
{
    Console.WriteLine(AppInfo.Title);
    return 0;
}

if (args.Length > 0 && (args[0] is "-h" or "--help" or "/?"))
{
    string exe = AppInfo.ExeName;
    Console.WriteLine(L.T($"{AppInfo.Title} — YouTube w terminalu, z wideo w Sixel, oktantach lub półblokach.",
        $"{AppInfo.Title} — YouTube in your terminal, with video as Sixel, octants or half blocks."));
    Console.WriteLine();
    Console.WriteLine(L.T($"Użycie: {exe} [fraza wyszukiwania | link do filmu/kanału/playlisty]",
        $"Usage: {exe} [search query | video/channel/playlist link]"));
    Console.WriteLine();
    Console.WriteLine(L.T("Opcje:", "Options:"));
    Console.WriteLine(L.T("  -v, --version   wersja programu", "  -v, --version   print the version"));
    Console.WriteLine(L.T("  -h, --help      ta pomoc", "  -h, --help      show this help"));
    Console.WriteLine();
    Console.WriteLine(L.T($"Dane aplikacji: {AppPaths.Root}", $"App data:    {AppPaths.Root}"));
    Console.WriteLine(L.T($"Deinstalator:   {Installer.UninstallerName} (obok pliku programu)",
        $"Uninstaller: {Installer.UninstallerName} (next to the program file)"));
    return 0;
}

if (OperatingSystem.IsWindows()) Console.OutputEncoding = Encoding.UTF8;
Log.InstallGlobalHandlers();
List<string> messages;
try
{
    messages = Installer.Run();
}
catch (Exception ex)
{
    messages = [L.T("Instalacja: ", "Setup: ") + ex.Message];
}

var app = new App(messages);
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--keys" && i + 1 < args.Length) app.KeyScript = args[++i];
    else rest.Add(args[i]);
}
if (rest.Count > 0) app.InitialQuery = string.Join(' ', rest);
try
{
    app.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(L.T("Błąd krytyczny: ", "Fatal error: ") + ex);
    try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:O}] {ex}\n"); } catch { }
    return 1;
}
return 0;
