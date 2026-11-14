using YoutubeTerminal.Graphics;
using YoutubeTerminal.Playback;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>Odtwarzacz pełnoekranowy: obraz Sixel/półbloki + pasek sterowania.</summary>
internal sealed class PlayerView : View
{
    private readonly VideoItem _video;
    private readonly IReadOnlyList<VideoItem>? _queue;
    private readonly int _index;
    private readonly TimeSpan? _start;
    private Player? _player;
    private bool _immersive;
    private bool _stats;
    private List<Chapter> _chapters = new();
    private VideoDetails? _details;
    private DateTime _lastSave = DateTime.UtcNow;
    private DateTime _lastTick;
    private volatile bool _changed;
    private string _osd = "";
    private DateTime _osdUntil;
    private bool _endedHandled;
    private readonly bool _audioOnly;
    private int _progressY = -1;
    private Rect _progressRect;

    public PlayerView(VideoItem v, IReadOnlyList<VideoItem>? queue, int index, TimeSpan? start, bool audioOnly)
    {
        _video = v;
        _queue = queue;
        _index = index;
        _start = start;
        _audioOnly = audioOnly;
    }

    public override bool FullScreen => true;
    public override string Title => "Odtwarzacz";

    public static int AutoHeightFor(Terminal t)
    {
        double pxH = Math.Min((t.Height - 3) * t.CellHeight, t.Width * t.CellWidth * 9.0 / 16.0) * App.Current.Settings.VideoScale / 100.0;
        if (App.Current.ImageMode == RenderMode.HalfBlock) return 360;
        return pxH <= 380 ? 360 : pxH <= 520 ? 480 : pxH <= 760 ? 720 : 1080;
    }

    private Rect VideoArea(Rect screen) => _immersive
        ? new Rect(screen.X, screen.Y, screen.W, screen.H - 1)
        : new Rect(screen.X, screen.Y, screen.W, screen.H - 3);

    public override void OnShow()
    {
        if (_player != null) return;
        var app = App;
        var opts = app.CreatePlayerOptions(_audioOnly);
        if (opts.MaxHeight == 0) opts.MaxHeight = AutoHeightFor(app.Term);
        _player = new Player(app.Term, _video, opts) { Volume = app.Settings.Volume };
        _player.Changed += () => { _changed = true; App.Current.Invalidate(); };
        _player.Ended += () => App.Current.Post(OnEnded);

        TimeSpan start = _start ?? TimeSpan.Zero;
        if (_start == null && app.Settings.ResumePlayback && app.Library.GetHistory(_video.Id) is { } h)
        {
            double dur = h.DurationSeconds > 0 ? h.DurationSeconds : _video.DurationSeconds;
            if (h.PositionSeconds > 10 && (dur <= 0 || h.PositionSeconds < dur - 15))
            {
                start = TimeSpan.FromSeconds(h.PositionSeconds);
                Osd($"Wznowiono od {TextUtil.FormatTime(start)} (Home — od początku)");
            }
        }
        var screen = new Rect(0, 0, app.Term.Width, app.Term.Height);
        _player.Start(start, VideoArea(screen));
        app.Term.SetTitle("▶ " + _video.Title);

        // Szczegóły (rozdziały, tytuł, autoodtwarzanie) — w tle.
        app.RunAsync(ct => app.YT.GetVideoAsync(_video.Id, ct), d =>
        {
            _details = d;
            _chapters = d.Chapters;
            if (_video.Title.Length == 0) _video.Title = d.Video.Title;
            if (_video.ChannelName.Length == 0) { _video.ChannelName = d.Video.ChannelName; _video.ChannelId = d.Video.ChannelId; }
        }, _ => { }, quiet: true);
    }

    public override void OnClose()
    {
        SaveHistory();
        _player?.Dispose();
        _player = null;
        App.Settings.Save();
        App.Term.SetTitle("YouTube Terminal");
    }

    private void SaveHistory()
    {
        var p = _player;
        if (p == null || !App.Settings.SaveHistory) return;
        if (!p.EverStarted && _start == null) return;
        if (p.IsLive)
        {
            App.Library.UpdateHistory(_video, 0, 0);
            App.Library.Save();
            return;
        }
        double pos = p.Position.TotalSeconds;
        double dur = p.Duration.TotalSeconds;
        if (p.State == PlayerState.Ended || (dur > 0 && pos > dur - 10)) pos = dur > 0 ? dur : pos;
        if (_video.DurationSeconds == 0 && dur > 0) _video.DurationSeconds = (int)dur;
        App.Library.UpdateHistory(_video, pos, dur);
        App.Library.Save();
    }

    private void Osd(string text)
    {
        _osd = text;
        _osdUntil = DateTime.UtcNow.AddSeconds(2.5);
        App.Invalidate();
    }

    public void OnScreenCleared() => _player?.RedrawLastFrame();

    private void OnEnded()
    {
        if (_endedHandled || _player == null) return;
        _endedHandled = true;
        SaveHistory();
        if (_queue != null && _index + 1 < _queue.Count)
        {
            PlayAt(_index + 1);
            return;
        }
        if (App.Settings.Autoplay)
        {
            var next = _details?.Related.Items.OfType<VideoItem>().FirstOrDefault(v => !v.IsShort && !v.IsLive && !v.IsUpcoming && v.Id != _video.Id);
            if (next != null)
            {
                App.ShowMessage("Autoodtwarzanie: " + next.Title);
                App.Play(next, null, 0, replace: true);
                return;
            }
        }
        Osd("Koniec filmu — Spacja odtwarza ponownie, q zamyka");
    }

    private void PlayAt(int idx)
    {
        if (_queue == null || idx < 0 || idx >= _queue.Count) return;
        SaveHistory();
        App.Play(_queue[idx], _queue, idx, replace: true);
    }

    public override bool Tick()
    {
        var now = DateTime.UtcNow;
        bool redraw = _changed;
        _changed = false;
        if ((now - _lastTick).TotalMilliseconds >= 250)
        {
            _lastTick = now;
            redraw = true;
        }
        if (_osd.Length > 0 && now > _osdUntil) { _osd = ""; redraw = true; }
        if ((now - _lastSave).TotalSeconds > 15 && _player?.State == PlayerState.Playing)
        {
            _lastSave = now;
            SaveHistory();
        }
        return redraw;
    }

    public override bool HandleKey(KeyEvent k)
    {
        var p = _player;
        if (p == null) return false;
        var app = App;
        bool shift = (k.Mods & Mods.Shift) != 0;

        if (p.IsLive)
        {
            bool seekKey = k.Key is Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home
                           || k.Is('j') || k.Is('l') || (k.Key == Key.Char && k.Char >= '0' && k.Char <= '9');
            if (seekKey) { Osd("Transmisja na żywo — przewijanie niedostępne (End — do bieżącego momentu)"); return true; }
            if (k.Key == Key.End) { p.JumpToLive(); Osd("● Na żywo"); return true; }
        }

        if (p.State == PlayerState.Error)
        {
            if (k.Is(' ') || k.Key == Key.Enter)
            {
                // Ponowna próba: nowy odtwarzacz od zapamiętanej pozycji.
                var pos = p.Position;
                p.Dispose();
                _player = null;
                OnShow();
                if (pos > TimeSpan.FromSeconds(1)) _player?.Seek(pos);
                return true;
            }
            if (k.Is('o')) { app.OpenInBrowser(_video); return true; }
        }

        switch (k.Key)
        {
            case Key.Left: p.SeekRelative(shift ? -60 : -5); Osd(shift ? "⏪ 60 s" : "⏪ 5 s"); return true;
            case Key.Right: p.SeekRelative(shift ? 60 : 5); Osd(shift ? "⏩ 60 s" : "⏩ 5 s"); return true;
            case Key.PageUp: p.SeekRelative(-60); Osd("⏪ 60 s"); return true;
            case Key.PageDown: p.SeekRelative(60); Osd("⏩ 60 s"); return true;
            case Key.Home: p.Seek(TimeSpan.Zero); Osd("⏮ Początek"); return true;
            case Key.End: p.Seek(p.Duration - TimeSpan.FromSeconds(10)); return true;
            case Key.Up: ChangeVolume(5); return true;
            case Key.Down: ChangeVolume(-5); return true;
            case Key.Escape: app.Pop(); return true;
            case Key.Enter: p.TogglePause(); return true;
        }

        if (k.Key != Key.Char || (k.Mods & (Mods.Ctrl | Mods.Alt)) != 0) return false;
        switch (k.Char)
        {
            case ' ':
            case 'k':
                p.TogglePause();
                return true;
            case 'j': p.SeekRelative(-10); Osd("⏪ 10 s"); return true;
            case 'l': p.SeekRelative(10); Osd("⏩ 10 s"); return true;
            case '+': case '=': ChangeVolume(5); return true;
            case '-': case '_': ChangeVolume(-5); return true;
            case 'm':
                p.Muted = !p.Muted;
                Osd(p.Muted ? "🔇 Wyciszono" : "🔊 Dźwięk włączony");
                return true;
            case 'f':
                _immersive = !_immersive;
                p.SetArea(VideoArea(new Rect(0, 0, app.Term.Width, app.Term.Height)));
                return true;
            case 'i': _stats = !_stats; return true;
            case 'n': if (_queue != null && _index + 1 < _queue.Count) PlayAt(_index + 1); else Osd("Brak następnego filmu w kolejce"); return true;
            case 'N': if (_queue != null && _index > 0) PlayAt(_index - 1); else Osd("Brak poprzedniego filmu"); return true;
            case '.': JumpChapter(1); return true;
            case ',': JumpChapter(-1); return true;
            case 'v':
                if (!app.SixelAvailable && p.Options.Mode == RenderMode.HalfBlock) { Osd("Terminal nie obsługuje Sixel"); return true; }
                p.Options.Mode = p.Options.Mode == RenderMode.Sixel ? RenderMode.HalfBlock : RenderMode.Sixel;
                p.ApplyOptions();
                Osd(p.Options.Mode == RenderMode.Sixel ? "Tryb: Sixel" : "Tryb: półbloki");
                return true;
            case 'd':
                p.Options.Dither = !p.Options.Dither;
                p.Options.AdaptivePalette = !p.Options.Dither;
                app.Settings.Dither = p.Options.Dither;
                p.ApplyOptions();
                Osd(p.Options.Dither ? "Paleta stała + dithering" : "Paleta adaptacyjna");
                return true;
            case '[':
            case ']':
                int sc = Math.Clamp(app.Settings.VideoScale + (k.Char == ']' ? 10 : -10), 30, 100);
                app.Settings.VideoScale = sc;
                p.Options.Scale = sc / 100.0;
                p.ApplyOptions();
                Osd($"Rozmiar obrazu: {sc}%");
                return true;
            case 'a':
                p.Options.AudioOnly = !p.Options.AudioOnly;
                p.ApplyOptions();
                Osd(p.Options.AudioOnly ? "♪ Tylko dźwięk" : "▶ Obraz i dźwięk");
                App.Invalidate();
                return true;
            case 'q': app.Pop(); return true;
            case 'c': app.Pop(); app.OpenChannel(_video.ChannelId, _video.ChannelName); return true;
            case 's': app.ToggleSubscribe(_video.ChannelId, _video.ChannelName); return true;
            case 'w': app.ToggleWatchLater(_video); return true;
            case 'y': app.CopyLink(_video); return true;
        }
        if (k.Char >= '0' && k.Char <= '9' && p.Duration > TimeSpan.Zero)
        {
            int pct = (k.Char - '0') * 10;
            p.Seek(TimeSpan.FromSeconds(p.Duration.TotalSeconds * pct / 100));
            Osd($"Skok do {pct}%");
            return true;
        }
        return false;
    }

    private void ChangeVolume(int delta)
    {
        var p = _player!;
        p.Volume = Math.Clamp(p.Volume + delta, 0, 100);
        p.Muted = false;
        App.Settings.Volume = p.Volume;
        Osd($"🔊 Głośność {p.Volume}%");
    }

    private void JumpChapter(int dir)
    {
        if (_chapters.Count == 0 || _player == null) { Osd("Ten film nie ma rozdziałów"); return; }
        double pos = _player.Position.TotalSeconds;
        Chapter? target;
        if (dir > 0) target = _chapters.FirstOrDefault(c => c.StartSeconds > pos + 1);
        else
        {
            var cur = _chapters.LastOrDefault(c => c.StartSeconds <= pos - 3);
            target = cur ?? _chapters[0];
        }
        if (target == null) { Osd("To ostatni rozdział"); return; }
        _player.Seek(TimeSpan.FromSeconds(target.StartSeconds));
        Osd("☰ " + target.Title);
    }

    private Chapter? CurrentChapter(double pos) => _chapters.LastOrDefault(c => c.StartSeconds <= pos);

    public override bool HandleMouse(MouseEvent m)
    {
        var p = _player;
        if (p == null) return false;
        if (m.Kind == MouseKind.WheelUp) { ChangeVolume(5); return true; }
        if (m.Kind == MouseKind.WheelDown) { ChangeVolume(-5); return true; }
        if (m.Kind == MouseKind.Press && m.Button == 0)
        {
            if (m.Y == _progressY && _progressRect.W > 0 && p.Duration > TimeSpan.Zero && m.X >= _progressRect.X && m.X < _progressRect.Right)
            {
                double frac = (m.X - _progressRect.X) / (double)Math.Max(1, _progressRect.W - 1);
                p.Seek(TimeSpan.FromSeconds(p.Duration.TotalSeconds * frac));
                return true;
            }
            if (m.Y < _progressY - 1) { p.TogglePause(); return true; }
        }
        return false;
    }

    public override void Render(ScreenBuffer s, Rect area)
    {
        var p = _player;
        if (p == null) return;
        var app = App;
        var va = VideoArea(area);
        p.SetArea(va);
        s.Fill(area, new Style(Theme.Text, 0x000000));

        bool showVideo = p.HasVideo && !p.Options.AudioOnly && p.EverStarted && p.State != PlayerState.Error;
        if (showVideo)
        {
            s.AddImage(new ImagePlacement { Area = va, External = true, Key = "video" });
        }
        else if (p.Options.AudioOnly && p.State is PlayerState.Playing or PlayerState.Paused or PlayerState.Buffering)
        {
            // Tryb audio: duża miniatura + informacja
            int rows = Math.Max(3, va.H - 6);
            var imgRect = new Rect(va.X + 4, va.Y + 2, va.W - 8, rows);
            var img = app.Images.Get(_video.LargeThumbnailUrl, imgRect, _video.ThumbnailUrl);
            if (img != null) s.AddImage(img);
            s.TextCenter(va, va.Bottom - 3, "♪  Odtwarzanie tylko dźwięku  (a — przywróć obraz)", new Style(Theme.Dim, 0x000000, Attr.Bold));
        }
        else
        {
            var msgStyle = p.State == PlayerState.Error ? new Style(0xFF6B6B, 0x000000, Attr.Bold) : new Style(Theme.Dim, 0x000000);
            int cy = va.Y + va.H / 2 - 2;
            s.TextCenter(va, cy, TextUtil.OneLine(_video.Title), new Style(Theme.Text, 0x000000, Attr.Bold));
            string msg = p.State == PlayerState.Error
                ? "✖ " + (p.Error ?? "Błąd odtwarzania")
                : app.Spinner + " " + (p.StatusText.Length > 0 ? p.StatusText : "Ładowanie…");
            foreach (var line in TextUtil.Wrap(msg, Math.Min(va.W - 4, 100)))
                s.TextCenter(va, cy += 2, line, msgStyle);
            if (p.State == PlayerState.Error)
                s.TextCenter(va, cy + 2, "q — zamknij · o — otwórz w przeglądarce · Spacja — spróbuj ponownie", Theme.MutedS with { Bg = 0x000000 });
        }

        RenderControls(s, area, p);
    }

    private void RenderControls(ScreenBuffer s, Rect area, Player p)
    {
        var app = App;
        var pos = p.Position;
        var dur = p.Duration;
        var bg = Theme.Panel;
        int rows = _immersive ? 1 : 3;
        var ctrl = new Rect(area.X, area.Bottom - rows, area.W, rows);
        s.Fill(ctrl, new Style(Theme.Text, bg));

        string stateIcon = p.State switch
        {
            PlayerState.Playing => "▶",
            PlayerState.Paused => "⏸",
            PlayerState.Ended => "⏹",
            PlayerState.Error => "✖",
            _ => app.Spinner,
        };
        if (p.Paused && p.State != PlayerState.Error) stateIcon = "⏸";
        string time = p.IsLive
            ? $"oglądasz {TextUtil.FormatTime(pos)}"
            : $"{TextUtil.FormatTime(pos)} / {(dur > TimeSpan.Zero ? TextUtil.FormatTime(dur) : "--:--")}";

        if (!_immersive)
        {
            int y0 = ctrl.Y;
            int x = ctrl.X + 2;
            string qinfo = _queue != null && _queue.Count > 1 ? $"[{_index + 1}/{_queue.Count}] " : "";
            if (qinfo.Length > 0) x += s.Text(x, y0, qinfo, new Style(Theme.Dim, bg));
            var ch = CurrentChapter(pos.TotalSeconds);
            string right = ch != null ? "☰ " + ch.Title : _video.ChannelName;
            int rw = Math.Min(TextUtil.Width(right), ctrl.W / 3);
            s.TextTrunc(x, y0, TextUtil.OneLine(_video.Title), new Style(Theme.Text, bg, Attr.Bold), ctrl.Right - x - rw - 4);
            s.TextRight(ctrl.Right - 2, y0, TextUtil.Truncate(right, rw), new Style(Theme.Dim, bg));
        }

        // Pasek postępu
        int py = _immersive ? ctrl.Y : ctrl.Y + 1;
        int timeW = TextUtil.Width(time);
        int barX = ctrl.X + 2 + 2 + (_immersive ? 0 : 0);
        int barW = ctrl.W - 4 - timeW - 4 - 2;
        if (_immersive) { s.Text(ctrl.X + 1, py, stateIcon, new Style(Theme.Text, bg)); barX = ctrl.X + 3; }
        _progressY = py;
        _progressRect = new Rect(barX, py, barW, 1);
        if (p.IsLive && barW > 4)
        {
            // Transmisja: pasek „na żywo” zamiast postępu.
            for (int i = 0; i < barW; i++) s.Put(barX + i, py, "━", new Style(0x5A1A22, bg));
            s.Text(barX, py, " ● NA ŻYWO ", Theme.LiveS);
            _progressRect = default;
        }
        else if (barW > 4)
        {
            double frac = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            int filled = (int)Math.Round(frac * (barW - 1));
            // Znaczniki rozdziałów
            var marks = new HashSet<int>();
            if (dur > TimeSpan.Zero)
                foreach (var c in _chapters)
                    if (c.StartSeconds > 0) marks.Add((int)Math.Round(c.StartSeconds / dur.TotalSeconds * (barW - 1)));
            for (int i = 0; i < barW; i++)
            {
                string chr = marks.Contains(i) ? "┃" : "━";
                int col = i < filled ? Theme.Accent : 0x4A4A4A;
                if (marks.Contains(i)) col = i < filled ? 0xFF8A9A : 0x7A7A7A;
                s.Put(barX + i, py, chr, new Style(col, bg));
            }
            s.Put(barX + filled, py, "●", new Style(Theme.Accent, bg, Attr.Bold));
        }
        s.TextRight(ctrl.Right - 2, py, time, new Style(Theme.Text, bg));

        if (!_immersive)
        {
            int y2 = ctrl.Y + 2;
            int x = ctrl.X + 2;
            x += s.Text(x, y2, stateIcon + " ", new Style(Theme.Text, bg, Attr.Bold));
            string vol = p.Muted ? "🔇 wycisz." : $"🔊 {p.Volume}%";
            x += s.Text(x + 1, y2, vol, new Style(Theme.Dim, bg)) + 3;
            string mode = p.Options.AudioOnly ? "♪ audio" : p.Options.Mode == RenderMode.Sixel ? "Sixel" : "Półbloki";
            x += s.Text(x, y2, mode, new Style(Theme.Dim, bg)) + 3;
            if (_stats)
            {
                string st = $"źródło {p.SourceInfo} · wyjście {p.VideoSize} · {p.MeasuredFps:0.0} fps · pominięte {p.Dropped}";
                s.TextTrunc(x, y2, st, new Style(Theme.Warn, bg), ctrl.Right - x - 2);
            }
            else
            {
                string osd = _osd.Length > 0 ? _osd : p.State == PlayerState.Buffering && p.EverStarted ? app.Spinner + " Buforowanie…" : "";
                if (osd.Length > 0) s.TextTrunc(x, y2, osd, new Style(Theme.Text, bg, Attr.Bold), ctrl.W / 2);
                string hints = "Spacja pauza  ←→ ±5s  ↑↓ głośność  , . rozdziały  f pełny ekran  i info  q zamknij";
                s.TextRight(ctrl.Right - 2, y2, TextUtil.Truncate(hints, Math.Max(0, ctrl.Right - x - TextUtil.Width(osd) - 6)), new Style(Theme.Muted, bg));
            }
        }
        else if (_osd.Length > 0)
        {
            // W trybie pełnoekranowym komunikat zastępuje czas.
            s.TextRight(ctrl.Right - 2 - timeW - 2, py, " " + _osd + " ", new Style(Theme.Text, bg, Attr.Bold));
        }
    }
}
