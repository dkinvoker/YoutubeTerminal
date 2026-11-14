using System.Collections.Concurrent;
using System.Diagnostics;
using YoutubeTerminal.Graphics;
using YoutubeTerminal.Playback;
using YoutubeTerminal.Storage;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

internal enum Section { Search, Subscriptions, History, WatchLater, Settings }

/// <summary>Główna pętla aplikacji: wejście, nawigacja między widokami, renderowanie.</summary>
internal sealed class App
{
    public static App Current { get; private set; } = null!;

    public Terminal Term { get; }
    public Settings Settings { get; }
    public Library Library { get; }
    public YouTubeClient YT { get; }
    public ImageService Images { get; }

    private readonly Renderer _renderer = new();
    private ScreenBuffer _screen = new(80, 25);
    private readonly BlockingCollection<InputEvent> _input = new();
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly AutoResetEvent _wake = new(false);
    private InputReader? _reader;
    private volatile bool _dirty = true;
    private bool _quit;
    private int _busy;
    private int _spinFrame;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastRender;

    private readonly Dictionary<Section, View> _roots = new();
    private readonly List<View> _stack = new();
    private Section _section = Section.Search;
    private Modal? _modal;
    private string _message = "";
    private bool _messageError;
    private DateTime _messageUntil;
    private readonly List<string> _startupMessages;

    /// <summary>Zapytanie lub link podany w linii poleceń.</summary>
    public string? InitialQuery { get; set; }

    /// <summary>Skrypt klawiszy do testów automatycznych (--keys), np. "wait:2000 text:abc key:enter dump:plik.txt".</summary>
    public string? KeyScript { get; set; }

    private void StartKeyScript(string script)
    {
        var tokens = script.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Task.Run(async () =>
        {
            foreach (var tok in tokens)
            {
                int c = tok.IndexOf(':');
                string cmd = c > 0 ? tok[..c] : tok, arg = c > 0 ? tok[(c + 1)..] : "";
                switch (cmd)
                {
                    case "wait":
                        await Task.Delay(int.Parse(arg));
                        break;
                    case "text":
                        foreach (char ch in arg.Replace('+', ' ')) _input.Add(new KeyEvent(Key.Char, ch));
                        break;
                    case "key":
                        var (key, ch2, mods) = arg switch
                        {
                            "enter" => (Key.Enter, '\0', Mods.None),
                            "esc" => (Key.Escape, '\0', Mods.None),
                            "up" => (Key.Up, '\0', Mods.None),
                            "down" => (Key.Down, '\0', Mods.None),
                            "left" => (Key.Left, '\0', Mods.None),
                            "right" => (Key.Right, '\0', Mods.None),
                            "tab" => (Key.Tab, '\0', Mods.None),
                            "btab" => (Key.BackTab, '\0', Mods.Shift),
                            "pgdn" => (Key.PageDown, '\0', Mods.None),
                            "pgup" => (Key.PageUp, '\0', Mods.None),
                            "space" => (Key.Char, ' ', Mods.None),
                            "f1" => (Key.F1, '\0', Mods.None),
                            "bs" => (Key.Backspace, '\0', Mods.None),
                            _ => (Key.Char, arg.Length > 0 ? arg[0] : ' ', Mods.None),
                        };
                        _input.Add(new KeyEvent(key, ch2, mods));
                        break;
                    case "dump":
                        Post(() => DumpScreen(arg));
                        break;
                    case "quit":
                        Post(() => _quit = true);
                        break;
                }
                _wake.Set();
            }
        });
    }

    /// <summary>Zapisuje tekstową zawartość ekranu (do testów).</summary>
    private void DumpScreen(string path)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < _screen.Height; y++)
        {
            for (int x = 0; x < _screen.Width; x++) sb.Append(_screen.Cells[y * _screen.Width + x].Text ?? "");
            sb.AppendLine();
        }
        sb.AppendLine($"--- obrazy: {_screen.Images.Count}");
        foreach (var i in _screen.Images) sb.AppendLine($"  {i.Area} ext={i.External} bytes={i.Data?.Length ?? 0}");
        sb.AppendLine($"--- sixel={SixelAvailable} mode={ImageMode} cell={CellWidth}x{CellHeight} fromTerm={Term.CellSizeFromTerminal} size={Term.Width}x{Term.Height}");
        sb.AppendLine($"--- status: {_message}");
        File.WriteAllText(path, sb.ToString());
    }

    public bool SixelAvailable { get; private set; }
    public RenderMode ImageMode { get; private set; } = RenderMode.HalfBlock;
    public int CellWidth => Term.CellWidth;
    public int CellHeight => Term.CellHeight;
    public bool ListThumbnails => Settings.ListThumbnails && ImageMode == RenderMode.Sixel;
    public string Spinner => "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"[_spinFrame % 10].ToString();
    public bool Busy => _busy > 0;

    public App(List<string> startupMessages)
    {
        Current = this;
        _startupMessages = startupMessages;
        Term = new Terminal();
        Settings = Settings.Load();
        Library = Library.Load();
        YT = new YouTubeClient { Language = Settings.Language, Region = Settings.Region };
        Images = new ImageService(YT.Http, Invalidate) { Background = Theme.Bg };
    }

    // ===================== Uruchomienie =====================

    public void Run()
    {
        Term.Init();
        Term.SetTitle("YouTube Terminal");
        _reader = new InputReader(Term, _input, () => _wake.Set());
        _reader.Start();
        DetectTerminal();
        ApplyGraphicsSettings();
        _ = YT.EnsureInitAsync();

        _screen = new ScreenBuffer(Term.Width, Term.Height);
        var search = new SearchView();
        _roots[Section.Search] = search;
        _stack.Add(search);
        search.OnShow();
        if (!string.IsNullOrWhiteSpace(InitialQuery)) search.Submit(InitialQuery);
        if (!string.IsNullOrWhiteSpace(KeyScript)) StartKeyScript(KeyScript);

        foreach (var m in _startupMessages) ShowError(m);
        if (YtDlp.FindExecutable() == null)
            ShowError("Nie znaleziono yt-dlp — przeglądanie działa, ale odtwarzanie jest niedostępne.");
        else if (!SixelAvailable && Settings.RenderMode == "auto")
            ShowMessage("Terminal nie zgłasza obsługi Sixel — używam trybu półbloków. Zalecany Windows Terminal 1.22+.");

        try
        {
            MainLoop();
        }
        finally
        {
            foreach (var v in _stack.ToArray()) { try { v.OnClose(); } catch { } }
            _reader.Dispose();
            Term.Dispose();
            Library.Save();
            Settings.Save();
        }
    }

    private void DetectTerminal()
    {
        // Zapytania: rozmiar komórki (16t), rozmiar okna w pikselach (14t), atrybuty urządzenia (DA1).
        Term.WriteString("\x1b[16t\x1b[14t\x1b[c");
        var sw = Stopwatch.StartNew();
        var deferred = new List<InputEvent>();
        int pxW = 0, pxH = 0;
        bool gotDa = false;
        while (sw.ElapsedMilliseconds < 1500 && !gotDa)
        {
            if (!_input.TryTake(out var ev, 50)) continue;
            if (ev is ReplyEvent r)
            {
                if (r.Final == 'c' && r.Prefix == '?')
                {
                    gotDa = true;
                    SixelAvailable = r.Params.Skip(1).Contains(4);
                }
                else if (r.Final == 't' && r.Params.Length >= 3)
                {
                    if (r.Params[0] == 6 && r.Params[1] > 0 && r.Params[2] > 0)
                    {
                        Term.CellHeight = r.Params[1];
                        Term.CellWidth = r.Params[2];
                        Term.CellSizeFromTerminal = true;
                    }
                    else if (r.Params[0] == 4) { pxH = r.Params[1]; pxW = r.Params[2]; }
                }
            }
            else deferred.Add(ev);
        }
        if (!Term.CellSizeFromTerminal && pxW > 0 && pxH > 0)
        {
            Term.UpdateSize();
            Term.CellWidth = Math.Max(4, pxW / Math.Max(1, Term.Width));
            Term.CellHeight = Math.Max(8, pxH / Math.Max(1, Term.Height));
            Term.CellSizeFromTerminal = true;
        }
        foreach (var e in deferred) _input.Add(e);
    }

    public void ApplyGraphicsSettings()
    {
        ImageMode = Settings.RenderMode switch
        {
            "sixel" => RenderMode.Sixel,
            "halfblock" => RenderMode.HalfBlock,
            _ => SixelAvailable ? RenderMode.Sixel : RenderMode.HalfBlock,
        };
        if (Settings.CellWidth > 0 && Settings.CellHeight > 0)
        {
            Term.CellWidth = Settings.CellWidth;
            Term.CellHeight = Settings.CellHeight;
        }
        else if (!Term.CellSizeFromTerminal)
        {
            Term.CellWidth = 10;
            Term.CellHeight = 20;
        }
        Images.Mode = ImageMode;
        Images.CellWidth = Term.CellWidth;
        Images.CellHeight = Term.CellHeight;
        Images.ClearEncoded();
        _renderer.Invalidate();
        Invalidate();
    }

    public PlayerOptions CreatePlayerOptions(bool audioOnly = false) => new()
    {
        MaxHeight = Settings.StreamQuality,
        MaxFps = Settings.MaxFps,
        Scale = Settings.VideoScale / 100.0,
        Mode = ImageMode,
        AdaptivePalette = !Settings.Dither,
        Dither = Settings.Dither,
        Colors = Settings.VideoColors,
        MaxWidthPx = Settings.MaxVideoWidth,
        AudioOnly = audioOnly,
        CellWidth = Term.CellWidth,
        CellHeight = Term.CellHeight,
        SyncOutput = Settings.SyncOutput,
    };

    // ===================== Pętla =====================

    private void MainLoop()
    {
        while (!_quit)
        {
            bool animating = _busy > 0 || Top is PlayerView;
            _wake.WaitOne(animating ? 40 : 200);

            while (_input.TryTake(out var ev))
            {
                try { HandleInput(ev); }
                catch (Exception ex) { Log.Error("UI", ex); ShowError("Błąd: " + ex.Message); }
                if (_quit) return;
            }
            while (_posted.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { Log.Error("UI", ex); ShowError("Błąd: " + ex.Message); }
            }

            if (Term.UpdateSize())
            {
                _screen = new ScreenBuffer(Term.Width, Term.Height);
                _renderer.Invalidate();
                _dirty = true;
            }

            long now = _clock.ElapsedMilliseconds;
            int frame = (int)(now / 90);
            if (_busy > 0 && frame != _spinFrame) { _spinFrame = frame; _dirty = true; }
            if (_message.Length > 0 && DateTime.UtcNow > _messageUntil) { _message = ""; _dirty = true; }
            try
            {
                if (Top.Tick()) _dirty = true;
            }
            catch (Exception ex) { Log.Error("UI", ex); ShowError("Błąd: " + ex.Message); }

            if (_dirty && now - _lastRender >= 15)
            {
                _dirty = false;
                _lastRender = now;
                Render();
            }
        }
    }

    private void Render()
    {
        var s = _screen;
        s.Clear(Theme.Normal);
        var top = Top;
        var full = s.Bounds;
        Rect area;
        if (top.FullScreen)
        {
            area = full;
        }
        else
        {
            DrawHeader(s, new Rect(0, 0, s.Width, 1));
            DrawStatus(s, new Rect(0, s.Height - 1, s.Width, 1));
            area = new Rect(0, 1, s.Width, Math.Max(0, s.Height - 2));
        }
        using (s.Clip(area))
            top.Render(s, area);

        if (_modal != null)
        {
            // Obrazy pod oknem modalnym byłyby rysowane na wierzchu — usuwamy je (oprócz wideo).
            s.Images.RemoveAll(i => !i.External);
            _modal.Render(s, full);
        }
        _renderer.Render(s, Term, Settings.SyncOutput);
        if (_renderer.FullRedrawHappened || _renderer.ExternalChanged)
            (top as PlayerView)?.OnScreenCleared();
    }

    private void DrawHeader(ScreenBuffer s, Rect r)
    {
        s.Fill(r, Theme.HeaderS);
        int x = r.X + 1;
        x += s.Text(x, r.Y, " ▶ ", new Style(0xFFFFFF, Theme.Accent, Attr.Bold));
        x += s.Text(x, r.Y, " YouTube", new Style(Theme.Text, Theme.Panel, Attr.Bold));
        x += s.Text(x, r.Y, " Terminal  ", new Style(Theme.Dim, Theme.Panel));
        (Section sec, string label)[] tabs =
        [
            (Section.Search, "Szukaj"), (Section.Subscriptions, "Subskrypcje"), (Section.History, "Historia"),
            (Section.WatchLater, "Do obejrzenia"), (Section.Settings, "Ustawienia"),
        ];
        _tabHits.Clear();
        for (int i = 0; i < tabs.Length; i++)
        {
            bool active = tabs[i].sec == _section;
            int start = x;
            var keySt = active ? new Style(0xFFFFFF, Theme.Accent, Attr.Bold) : new Style(Theme.Dim, 0x2A2A2A, Attr.Bold);
            x += s.Text(x, r.Y, $" {i + 1} ", keySt);
            x += s.Text(x, r.Y, " " + tabs[i].label + " ", active ? new Style(Theme.Text, Theme.Panel, Attr.Bold) : new Style(Theme.Dim, Theme.Panel));
            _tabHits.Add((start, x, tabs[i].sec));
            x += 1;
        }
        // Ścieżka (okruszki) po prawej
        var crumbs = string.Join(" › ", _stack.Skip(1).Select(v => v.Title).Where(t => t.Length > 0));
        if (crumbs.Length > 0)
        {
            int avail = r.Right - x - 2;
            if (avail > 6) s.TextRight(r.Right - 1, r.Y, TextUtil.Truncate(crumbs, avail), new Style(Theme.Dim, Theme.Panel));
        }
    }

    private readonly List<(int X0, int X1, Section Sec)> _tabHits = new();

    private void DrawStatus(ScreenBuffer s, Rect r)
    {
        s.Fill(r, Theme.StatusS);
        int x = r.X + 1;
        string right = "";
        Style rightSt = Theme.StatusS;
        if (_message.Length > 0)
        {
            right = _message;
            rightSt = _messageError ? new Style(0xFF6B6B, Theme.Panel, Attr.Bold) : new Style(Theme.Text, Theme.Panel);
        }
        else if (_busy > 0) right = Spinner + " Wczytywanie…";

        int rightW = Math.Min(TextUtil.Width(right), r.W / 2 + 10);
        int limit = r.Right - rightW - 2;
        foreach (var (key, label) in Top.Hints.Concat(GlobalHints()))
        {
            int need = TextUtil.Width(key) + TextUtil.Width(label) + 4;
            if (x + need > limit) break;
            x += s.Text(x, r.Y, " " + key + " ", Theme.KeyS);
            x += s.Text(x, r.Y, " " + label + "  ", Theme.StatusS);
        }
        if (right.Length > 0) s.TextRight(r.Right - 1, r.Y, TextUtil.Truncate(right, rightW), rightSt);
    }

    private IEnumerable<(string, string)> GlobalHints()
    {
        yield return ("F1", "Pomoc");
        yield return (_stack.Count > 1 ? "Esc" : "q", _stack.Count > 1 ? "Wstecz" : "Wyjście");
    }

    // ===================== Wejście =====================

    private void HandleInput(InputEvent ev)
    {
        _dirty = true;
        switch (ev)
        {
            case ResizeEvent:
                if (Term.UpdateSize())
                {
                    _screen = new ScreenBuffer(Term.Width, Term.Height);
                    _renderer.Invalidate();
                }
                return;
            case PasteEvent p:
                if (_modal == null) Top.HandlePaste(p.Text);
                return;
            case MouseEvent m:
                if (_modal != null) { _modal.HandleMouse(m); return; }
                if (m.Kind == MouseKind.Press && m.Y == 0 && !Top.FullScreen)
                {
                    foreach (var (x0, x1, sec) in _tabHits)
                        if (m.X >= x0 && m.X < x1) { SwitchSection(sec); return; }
                }
                Top.HandleMouse(m);
                return;
            case KeyEvent k:
                HandleKey(k);
                return;
        }
    }

    private void HandleKey(KeyEvent k)
    {
        if (k.IsCtrl('c') || k.IsCtrl('q')) { _quit = true; return; }
        if (k.IsCtrl('l')) { _renderer.Invalidate(); return; }

        if (_modal != null)
        {
            if (_modal.HandleKey(k)) _modal = null;
            return;
        }

        var top = Top;
        if (top.HandleKey(k)) return;

        if (k.Key == Key.F1 || (k.Is('?') && !top.CapturesText)) { _modal = new HelpModal(); return; }
        if (k.Key == Key.Escape || k.Key == Key.Backspace)
        {
            if (_stack.Count > 1) Pop();
            return;
        }
        if (top.CapturesText) return;

        if (k.Is('q'))
        {
            if (_stack.Count > 1) Pop();
            else _quit = true;
            return;
        }
        if (k.Key == Key.Char && k.Mods == Mods.None && k.Char >= '1' && k.Char <= '5')
        {
            SwitchSection((Section)(k.Char - '1'));
            return;
        }
        if (k.Is('/'))
        {
            SwitchSection(Section.Search);
            (Top as SearchView)?.FocusInput();
        }
    }

    // ===================== Nawigacja =====================

    public View Top => _stack[^1];

    public void Push(View v)
    {
        Top.OnHide();
        _stack.Add(v);
        v.OnShow();
        Invalidate();
    }

    public void Pop()
    {
        if (_stack.Count <= 1) return;
        var v = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        v.OnHide();
        v.OnClose();
        Top.OnShow();
        Invalidate();
    }

    /// <summary>Zastępuje bieżący widok (np. następny film w odtwarzaczu).</summary>
    public void Replace(View v)
    {
        var old = _stack[^1];
        _stack[^1] = v;
        old.OnHide();
        old.OnClose();
        v.OnShow();
        Invalidate();
    }

    public void SwitchSection(Section sec)
    {
        // Zamykamy widoki z bieżącej sekcji (np. odtwarzacz), korzeń zostaje.
        while (_stack.Count > 1) Pop();
        if (sec == _section) { Invalidate(); return; }
        Top.OnHide();
        _section = sec;
        if (!_roots.TryGetValue(sec, out var root))
        {
            root = sec switch
            {
                Section.Subscriptions => new SubscriptionsView(),
                Section.History => new HistoryView(),
                Section.WatchLater => new WatchLaterView(),
                Section.Settings => new SettingsView(),
                _ => new SearchView(),
            };
            _roots[sec] = root;
        }
        _stack[0] = root;
        root.OnShow();
        Invalidate();
    }

    public void ShowModal(Modal m)
    {
        _modal = m;
        Invalidate();
    }

    public void Confirm(string title, string message, Action onYes) => ShowModal(new ConfirmModal(title, message, onYes));

    // ===================== Akcje wspólne =====================

    public void OpenItem(YtItem item)
    {
        switch (item)
        {
            case VideoItem v: OpenVideo(v); break;
            case ChannelItem c: OpenChannel(c.Id, c.Title); break;
            case PlaylistItem p: OpenPlaylist(p); break;
        }
    }

    public void OpenVideo(VideoItem v) => Push(new VideoView(v));

    public void OpenChannel(string channelId, string? title = null)
    {
        if (string.IsNullOrEmpty(channelId)) { ShowError("Brak informacji o kanale."); return; }
        Push(new ChannelView(channelId, title));
    }

    public void OpenPlaylist(PlaylistItem p) => Push(new PlaylistView(p));

    /// <summary>Uruchamia odtwarzacz. <paramref name="queue"/> — kolejka do autoodtwarzania.</summary>
    public void Play(VideoItem v, IReadOnlyList<VideoItem>? queue = null, int index = 0, TimeSpan? start = null, bool audioOnly = false, bool replace = false)
    {
        if (YtDlp.FindExecutable() == null)
        {
            ShowError("Odtwarzanie wymaga yt-dlp.exe (nie znaleziono).");
            return;
        }
        if (v.IsUpcoming) { ShowError("To premiera, która jeszcze się nie rozpoczęła."); return; }
        var view = new PlayerView(v, queue, index, start, audioOnly);
        if (replace || Top is PlayerView) Replace(view);
        else Push(view);
    }

    public void PlayItem(YtItem item, IReadOnlyList<YtItem>? context = null, bool audioOnly = false)
    {
        switch (item)
        {
            case VideoItem v:
                var queue = v.IsLive ? null : context?.OfType<VideoItem>().Where(x => !x.IsLive && !x.IsUpcoming).ToList();
                int idx = queue?.FindIndex(x => x.Id == v.Id) ?? -1;
                if (idx < 0) { queue = null; idx = 0; }
                Play(v, queue, idx, audioOnly: audioOnly);
                break;
            case PlaylistItem p:
                RunAsync(ct => YT.GetPlaylistAsync(p.Id, p.FirstVideoId, ct), page =>
                {
                    var vids = page.Content.Items.OfType<VideoItem>().ToList();
                    if (vids.Count == 0) { ShowError("Playlista jest pusta."); return; }
                    Play(vids[0], vids, 0, audioOnly: audioOnly);
                });
                break;
            case ChannelItem c:
                OpenChannel(c.Id, c.Title);
                break;
        }
    }

    public void ToggleSubscribe(ChannelItem c)
    {
        bool now = Library.ToggleSubscription(c);
        ShowMessage(now ? $"Subskrybujesz: {c.Title}" : $"Anulowano subskrypcję: {c.Title}");
        (_roots.GetValueOrDefault(Section.Subscriptions) as SubscriptionsView)?.MarkDirty();
    }

    public void ToggleSubscribe(string channelId, string title)
    {
        if (string.IsNullOrEmpty(channelId)) { ShowError("Brak informacji o kanale."); return; }
        ToggleSubscribe(new ChannelItem { Id = channelId, Title = title });
    }

    public void ToggleWatchLater(VideoItem v)
    {
        bool now = Library.ToggleWatchLater(v);
        ShowMessage(now ? "Dodano do „Do obejrzenia”" : "Usunięto z „Do obejrzenia”");
    }

    public static string UrlOf(YtItem item) => item switch
    {
        VideoItem v => v.Url,
        ChannelItem c => c.Url,
        PlaylistItem p => p.Url,
        _ => "",
    };

    public void CopyLink(YtItem item)
    {
        Term.CopyToClipboard(UrlOf(item));
        ShowMessage("Skopiowano link: " + UrlOf(item));
    }

    public void OpenInBrowser(YtItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UrlOf(item)) { UseShellExecute = true });
            ShowMessage("Otwarto w przeglądarce");
        }
        catch (Exception ex)
        {
            ShowError("Nie udało się otworzyć przeglądarki: " + ex.Message);
        }
    }

    /// <summary>Obsługa wspólnych klawiszy dla zaznaczonego elementu listy.</summary>
    public bool HandleItemKey(KeyEvent k, YtItem? item, IReadOnlyList<YtItem>? context)
    {
        if (item == null) return false;
        if (k.Key == Key.Enter) { OpenItem(item); return true; }
        if (k.Is('p') || k.Is(' ')) { PlayItem(item, context); return true; }
        if (k.Is('a')) { PlayItem(item, context, audioOnly: true); return true; }
        if (k.Is('y')) { CopyLink(item); return true; }
        if (k.Is('o')) { OpenInBrowser(item); return true; }
        if (k.Is('w') && item is VideoItem wv) { ToggleWatchLater(wv); return true; }
        if (k.Is('c'))
        {
            switch (item)
            {
                case VideoItem v: OpenChannel(v.ChannelId, v.ChannelName); return true;
                case PlaylistItem p: OpenChannel(p.ChannelId, p.ChannelName); return true;
                case ChannelItem ch: OpenChannel(ch.Id, ch.Title); return true;
            }
        }
        if (k.Is('s'))
        {
            switch (item)
            {
                case ChannelItem ch: ToggleSubscribe(ch); return true;
                case VideoItem v: ToggleSubscribe(v.ChannelId, v.ChannelName); return true;
                case PlaylistItem p: ToggleSubscribe(p.ChannelId, p.ChannelName); return true;
            }
        }
        return false;
    }

    public static IEnumerable<(string, string)> ItemHints(YtItem? item)
    {
        if (item == null) yield break;
        yield return ("Enter", "Otwórz");
        if (item is not ChannelItem) yield return ("p", "Odtwórz");
        if (item is VideoItem) yield return ("w", "Do obejrzenia");
        yield return ("c", "Kanał");
        yield return ("s", "Subskrybuj");
    }

    // ===================== Wątki / komunikaty =====================

    public void Post(Action a)
    {
        _posted.Enqueue(a);
        _wake.Set();
    }

    public void Invalidate()
    {
        _dirty = true;
        _wake.Set();
    }

    public void ShowMessage(string msg, int seconds = 4)
    {
        _message = msg;
        _messageError = false;
        _messageUntil = DateTime.UtcNow.AddSeconds(seconds);
        Invalidate();
    }

    public void ShowError(string msg, int seconds = 8)
    {
        _message = msg;
        _messageError = true;
        _messageUntil = DateTime.UtcNow.AddSeconds(seconds);
        Invalidate();
    }

    /// <summary>Wykonuje pracę w tle, a wynik przekazuje do wątku UI.</summary>
    public void RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, Action<Exception>? onError = null,
        bool quiet = false, CancellationToken ct = default)
    {
        if (!quiet) Interlocked.Increment(ref _busy);
        Invalidate();
        Task.Run(async () =>
        {
            try
            {
                var result = await work(ct).ConfigureAwait(false);
                Post(() =>
                {
                    if (!quiet) _busy--;
                    if (!ct.IsCancellationRequested) onDone(result);
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    if (!quiet) _busy--;
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) return;
                    if (onError != null) onError(ex);
                    else ShowError(ex.Message);
                });
            }
        });
    }

    public void Quit() => _quit = true;
}
