using YoutubeTerminal.Tui;

namespace YoutubeTerminal.UI;

/// <summary>Ciemny motyw w stylu YouTube.</summary>
internal static class Theme
{
    public const int Bg = 0x0F0F0F;
    public const int Panel = 0x181818;
    public const int Raised = 0x212121;
    public const int Selection = 0x2E2E2E;
    public const int SelectionFocus = 0x3A3A3A;
    public const int Border = 0x3A3A3A;
    public const int Text = 0xF1F1F1;
    public const int Dim = 0xAAAAAA;
    public const int Muted = 0x717171;
    public const int Accent = 0xFF0033;
    public const int AccentSoft = 0xCC2A41;
    public const int Live = 0xCC0000;
    public const int Link = 0x3EA6FF;
    public const int Good = 0x2BA640;
    public const int Warn = 0xF2C94C;

    public static readonly Style Normal = new(Text, Bg);
    public static readonly Style DimS = new(Dim, Bg);
    public static readonly Style MutedS = new(Muted, Bg);
    public static readonly Style Bold = new(Text, Bg, Attr.Bold);
    public static readonly Style AccentS = new(Accent, Bg, Attr.Bold);
    public static readonly Style BorderS = new(Border, Bg);
    public static readonly Style HeaderS = new(Text, Panel);
    public static readonly Style StatusS = new(Dim, Panel);
    public static readonly Style KeyS = new(Text, 0x3A3A3A, Attr.Bold);
    public static readonly Style BadgeS = new(Text, 0x303030, Attr.Bold);
    public static readonly Style LiveS = new(0xFFFFFF, Live, Attr.Bold);
    public static readonly Style LinkS = new(Link, Bg);
}
