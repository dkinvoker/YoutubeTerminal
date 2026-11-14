using YoutubeTerminal.Playback;
using YoutubeTerminal.Storage;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.UI;

/// <summary>Ustawienia aplikacji (zapisywane w %LOCALAPPDATA%\YoutubeTerminal\settings.json).</summary>
internal sealed class SettingsView : View
{
    private sealed record Option(string Label, Func<string> Value, Action<int> Change, string Help);

    private readonly List<Option> _options = new();
    private int _sel;
    private string _ytDlpVersion = "…";
    private bool _updating;

    public override string Title => "Ustawienia";

    public SettingsView()
    {
        var s = App.Settings;
        _options.Add(new("Tryb grafiki", () => s.RenderMode switch { "sixel" => "Sixel", "halfblock" => "Półbloki ▀", _ => $"Automatycznie ({(App.SixelAvailable ? "Sixel" : "półbloki")})" },
            d => { s.RenderMode = Cycle(["auto", "sixel", "halfblock"], s.RenderMode, d); App.ApplyGraphicsSettings(); },
            "Sixel daje pełną grafikę w Windows Terminal 1.22+. Półbloki działają w każdym terminalu z kolorem 24-bit."));
        _options.Add(new("Miniatury na listach", () => YesNo(s.ListThumbnails),
            _ => { s.ListThumbnails = !s.ListThumbnails; App.Invalidate(); },
            "Małe miniatury Sixel obok każdego wyniku (tylko w trybie Sixel)."));
        _options.Add(new("Jakość strumienia", () => s.StreamQuality == 0 ? "Automatycznie (wg rozmiaru okna)" : s.StreamQuality + "p",
            d => s.StreamQuality = Cycle([0, 360, 480, 720, 1080], s.StreamQuality, d),
            "Wyższa jakość = ostrzejszy obraz, ale więcej danych i obliczeń. Automatycznie dobiera do rozmiaru terminala."));
        _options.Add(new("Limit klatek na sekundę", () => s.MaxFps + " fps",
            d => s.MaxFps = Cycle([12, 15, 20, 24, 30, 60], s.MaxFps, d),
            "Ogranicza liczbę klatek wysyłanych do terminala — zmniejsz, jeśli obraz się zacina."));
        _options.Add(new("Rozmiar obrazu wideo", () => s.VideoScale + "%",
            d => s.VideoScale = Cycle([30, 40, 50, 60, 70, 80, 90, 100], s.VideoScale, d),
            "Mniejszy obraz to mniej danych Sixel na klatkę — płynniejsze odtwarzanie w wolniejszych terminalach."));
        _options.Add(new("Maks. szerokość obrazu wideo", () => s.MaxVideoWidth == 0 ? "Bez limitu" : s.MaxVideoWidth + " px",
            d => s.MaxVideoWidth = Cycle([480, 640, 800, 960, 1280, 1600, 1920, 0], s.MaxVideoWidth, d),
            "Ogranicza rozmiar klatek Sixel w dużych oknach. Większy obraz wymaga szybszego terminala."));
        _options.Add(new("Paleta kolorów", () => s.Dither ? "Stała 252 kolory + dithering" : "Adaptacyjna (median-cut)",
            _ => s.Dither = !s.Dither,
            "Paleta adaptacyjna daje najlepszą jakość; stała z ditheringiem jest stabilniejsza przy gradientach."));
        _options.Add(new("Kolory w klatce wideo", () => s.VideoColors + (s.VideoColors >= 255 ? " (maks.)" : ""),
            d => s.VideoColors = Cycle([32, 64, 96, 128, 192, 255], s.VideoColors, d),
            "Liczba kolorów palety adaptacyjnej dla wideo. Mniej kolorów = mniejsze klatki i płynniejszy obraz."));
        _options.Add(new("Autoodtwarzanie", () => YesNo(s.Autoplay), _ => s.Autoplay = !s.Autoplay,
            "Po zakończeniu filmu odtwarza następny z kolejki lub pierwszy z podobnych."));
        _options.Add(new("Wznawianie od miejsca przerwania", () => YesNo(s.ResumePlayback), _ => s.ResumePlayback = !s.ResumePlayback,
            "Rozpoczyna odtwarzanie od zapamiętanej pozycji z historii."));
        _options.Add(new("Zapisuj historię oglądania", () => YesNo(s.SaveHistory), _ => s.SaveHistory = !s.SaveHistory,
            "Historia jest przechowywana tylko lokalnie."));
        _options.Add(new("Synchronizowane wyjście (DEC 2026)", () => YesNo(s.SyncOutput), _ => s.SyncOutput = !s.SyncOutput,
            "Zapobiega rwaniu obrazu w terminalach obsługujących tryb 2026."));
        _options.Add(new("Język i region wyników", () => $"{s.Language}-{s.Region}",
            d =>
            {
                var v = Cycle(["pl-PL", "en-US", "en-GB", "de-DE", "uk-UA", "cs-CZ", "fr-FR", "es-ES"], $"{s.Language}-{s.Region}", d);
                s.Language = v[..2];
                s.Region = v[3..];
                App.YT.Language = s.Language;
                App.YT.Region = s.Region;
            },
            "Wpływa na język interfejsu YouTube (daty, liczby wyświetleń) i lokalne wyniki."));
        _options.Add(new("Rozmiar komórki (px)", () => s.CellWidth > 0 ? $"{s.CellWidth}×{s.CellHeight}" : $"Automatycznie ({App.CellWidth}×{App.CellHeight})",
            d =>
            {
                var cur = s.CellWidth > 0 ? $"{s.CellWidth}x{s.CellHeight}" : "auto";
                var v = Cycle(["auto", "8x16", "9x18", "10x20", "11x22", "12x24", "14x28"], cur, d);
                if (v == "auto") { s.CellWidth = 0; s.CellHeight = 0; }
                else { var p = v.Split('x'); s.CellWidth = int.Parse(p[0]); s.CellHeight = int.Parse(p[1]); }
                App.ApplyGraphicsSettings();
            },
            "Rozmiar komórki w pikselach dla Sixel. Zmień, jeśli obrazy mają złe proporcje lub nachodzą na tekst."));
        _options.Add(new("yt-dlp", () => _updating ? App.Spinner + " aktualizacja…" : $"{_ytDlpVersion}  (Enter — aktualizuj)",
            _ => UpdateYtDlp(),
            $"Wbudowany yt-dlp pobiera adresy strumieni. Wypakowany do: {Installer.YtDlpPath}"));
    }

    public override void OnShow()
    {
        App.RunAsync(ct => YtDlp.VersionAsync(ct), v => _ytDlpVersion = v, _ => _ytDlpVersion = "brak", quiet: true);
    }

    public override void OnHide() => App.Settings.Save();

    private void UpdateYtDlp()
    {
        if (_updating) return;
        _updating = true;
        App.RunAsync(ct => YtDlp.UpdateAsync(ct), r =>
        {
            _updating = false;
            App.ShowMessage("yt-dlp: " + r, 8);
            OnShow();
        }, ex => { _updating = false; App.ShowError("Aktualizacja yt-dlp nie powiodła się: " + ex.Message); });
    }

    private static string YesNo(bool b) => b ? "Tak" : "Nie";

    private static T Cycle<T>(T[] values, T current, int dir)
    {
        int i = Array.IndexOf(values, current);
        if (i < 0) i = 0;
        else i = (i + (dir < 0 ? -1 : 1) + values.Length) % values.Length;
        return values[i];
    }

    public override IEnumerable<(string Key, string Label)> Hints
    {
        get
        {
            yield return ("↑↓", "Wybór");
            yield return ("← → Enter", "Zmień");
        }
    }

    public override bool HandleKey(KeyEvent k)
    {
        switch (k.Key)
        {
            case Key.Up: _sel = Math.Max(0, _sel - 1); return true;
            case Key.Down: _sel = Math.Min(_options.Count - 1, _sel + 1); return true;
            case Key.Left: _options[_sel].Change(-1); App.Settings.Save(); return true;
            case Key.Right:
            case Key.Enter: _options[_sel].Change(1); App.Settings.Save(); return true;
        }
        if (k.Is(' ')) { _options[_sel].Change(1); App.Settings.Save(); return true; }
        return false;
    }

    public override bool HandleMouse(MouseEvent m)
    {
        if (m.Kind == MouseKind.Press && m.Y >= _listY && m.Y < _listY + _options.Count)
        {
            int idx = m.Y - _listY;
            if (idx == _sel) { _options[_sel].Change(1); App.Settings.Save(); }
            _sel = idx;
            return true;
        }
        return false;
    }

    private int _listY;

    public override void Render(ScreenBuffer s, Rect area)
    {
        Layouts.SectionHeader(s, area, "Ustawienia", "zmiany są zapisywane od razu");
        int y = area.Y + 4;
        _listY = y;
        int labelW = 36;
        int w = Math.Min(area.W - 4, 110);
        for (int i = 0; i < _options.Count && y < area.Bottom - 6; i++, y++)
        {
            var o = _options[i];
            bool sel = i == _sel;
            var st = new Style(Theme.Text, sel ? Theme.SelectionFocus : Theme.Bg, sel ? Attr.Bold : Attr.None);
            s.Fill(new Rect(area.X + 2, y, w, 1), st);
            if (sel) s.Put(area.X + 2, y, "▌", new Style(Theme.Accent, st.Bg));
            s.TextTrunc(area.X + 4, y, o.Label, st, labelW);
            s.TextTrunc(area.X + 4 + labelW, y, (sel ? "◀ " : "  ") + o.Value() + (sel ? " ▶" : ""), new Style(sel ? Theme.Text : Theme.Dim, st.Bg), w - labelW - 4);
        }

        y += 1;
        if (y < area.Bottom - 1)
            y += s.Paragraph(new Rect(area.X + 4, y, w - 4, 3), _options[_sel].Help, Theme.DimS) + 1;

        var info = new List<string>
        {
            $"Wersja: {AppInfo.Title}",
            $"Terminal: {App.Term.Width}×{App.Term.Height} komórek, komórka {App.CellWidth}×{App.CellHeight} px{(App.Term.CellSizeFromTerminal ? " (zgłoszona przez terminal)" : " (domyślna)")}, Sixel: {(App.SixelAvailable ? "tak" : "nie")}",
            $"Dane aplikacji: {AppPaths.Root}",
            $"Deinstalator: {Path.Combine(Path.GetDirectoryName(AppPaths.ExePath) ?? "", Installer.UninstallerName)}",
        };
        foreach (var line in info)
        {
            if (y >= area.Bottom) break;
            s.TextTrunc(area.X + 4, y++, line, Theme.MutedS, area.W - 6);
        }
    }
}
