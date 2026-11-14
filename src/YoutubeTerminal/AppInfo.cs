namespace YoutubeTerminal;

/// <summary>Informacje o wersji programu (z atrybutów zestawu — działa także w Native AOT).</summary>
internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public static string Name => "YouTube Terminal";

    public static string Title => $"{Name} {Version}";
}
