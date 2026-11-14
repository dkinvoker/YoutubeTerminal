using System.Text;
using YoutubeTerminal;
using YoutubeTerminal.Storage;
using YoutubeTerminal.UI;

if (args.Length > 0 && args[0].StartsWith("--diag", StringComparison.Ordinal))
    return await Diagnostics.RunAsync(args);

if (args.Length > 0 && (args[0] is "-h" or "--help" or "/?"))
{
    Console.WriteLine("YouTube Terminal — przeglądarka YouTube w terminalu z wideo przez Sixel.");
    Console.WriteLine();
    Console.WriteLine("Użycie: YoutubeTerminal.exe [fraza wyszukiwania | link do filmu/kanału/playlisty]");
    Console.WriteLine();
    Console.WriteLine($"Dane aplikacji: {AppPaths.Root}");
    Console.WriteLine($"Deinstalator:   {Installer.UninstallerName} (obok pliku .exe)");
    return 0;
}

Console.OutputEncoding = Encoding.UTF8;
Log.InstallGlobalHandlers();
List<string> messages;
try
{
    messages = Installer.Run();
}
catch (Exception ex)
{
    messages = ["Instalacja: " + ex.Message];
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
    Console.Error.WriteLine("Błąd krytyczny: " + ex);
    try { File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:O}] {ex}\n"); } catch { }
    return 1;
}
return 0;
