using System.Collections.Concurrent;
using System.Diagnostics;
using YoutubeTerminal.Graphics;
using YoutubeTerminal.Native;
using YoutubeTerminal.Tui;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.Playback;

internal enum PlayerState { Resolving, Buffering, Playing, Paused, Ended, Error }

internal sealed class PlayerOptions
{
    public int MaxHeight = 480;          // 0 = automatycznie
    public int MaxFps = 30;
    public double Scale = 1.0;
    public RenderMode Mode = RenderMode.Sixel;
    public bool AdaptivePalette = true;
    public bool Dither;
    public int Colors = 128;
    public int MaxWidthPx = 960;
    public bool AudioOnly;
    public int CellWidth = 10, CellHeight = 20;
    public bool SyncOutput = true;
}

/// <summary>
/// Odtwarzacz: rozwiązuje strumienie (yt-dlp), demultipleksuje MP4, dekoduje przez Media Foundation,
/// odtwarza dźwięk przez waveOut i wyświetla klatki (Sixel/półbloki) zsynchronizowane z zegarem audio.
/// </summary>
internal sealed class Player : IDisposable
{
    private readonly Terminal _term;
    private readonly object _lock = new();
    private Session? _session;
    private CancellationTokenSource _cts = new();
    private IMediaSource? _videoDemux, _audioDemux;
    private Func<(IMediaSource? Video, IMediaSource? Audio)>? _liveFactory;
    private Timer? _restartTimer;
    private TimeSpan _pendingPosition;
    private bool _disposed;
    private Rect _area;

    public event Action? Changed;
    public event Action? Ended;

    public VideoItem Video { get; }
    public PlayerOptions Options { get; }
    public StreamInfo? Stream { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Resolving;
    public string StatusText { get; private set; } = "Pobieranie informacji o strumieniu…";
    public string? Error { get; private set; }
    public TimeSpan Duration { get; private set; }
    private int _volume = 80;
    private bool _muted;

    /// <summary>Głośność 0–100; zmiana działa natychmiast (głośność ustawiana na wyjściu audio).</summary>
    public int Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 100); _session?.ApplyVolume(); }
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; _session?.ApplyVolume(); }
    }
    public bool Paused { get; private set; }
    public bool Seeking => _restartTimer != null;

    public int Presented => _session?.Presented ?? 0;
    public int Dropped => _session?.Dropped ?? 0;
    public double MeasuredFps => _session?.MeasuredFps ?? 0;
    public string VideoSize => _session is { } s ? $"{s.PixelWidth}×{s.PixelHeight}" : "";
    private string AudioCodecName => Stream?.AudioCodec is { Length: > 0 } a && a != "unknown" ? a : "AAC";

    public string SourceInfo => Stream == null ? "" : Stream.VideoUrl == null
        ? $"audio {AudioCodecName}"
        : $"{Stream.Width}×{Stream.Height} {Stream.Fps:0}fps {Stream.VideoCodec} / {AudioCodecName}{(IsLive ? " · HLS" : "")}";

    /// <summary>Głośność z krzywą percepcyjną (0..1).</summary>
    public float EffectiveVolume => Muted ? 0f : MathF.Pow(Math.Clamp(Volume / 100f, 0f, 1f), 1.7f);

    public bool IsLive { get; private set; }

    public TimeSpan Position
    {
        get
        {
            if (_restartTimer != null || _session == null) return IsLive ? TimeSpan.Zero : _pendingPosition;
            if (IsLive) return _session.LiveElapsed;
            var p = _session.Clock;
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }

    public Player(Terminal term, VideoItem video, PlayerOptions options)
    {
        _term = term;
        Video = video;
        Options = options;
        Duration = TimeSpan.FromSeconds(video.DurationSeconds);
    }

    public Rect Area => _area;

    /// <summary>Rozpoczyna odtwarzanie od podanej pozycji.</summary>
    public void Start(TimeSpan start, Rect area)
    {
        _area = area;
        _pendingPosition = start;
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                SetState(PlayerState.Resolving, "Pobieranie informacji o strumieniu (yt-dlp)…");
                int maxH = Options.MaxHeight > 0 ? Options.MaxHeight : AutoHeight(area);
                // Zawsze pobieramy obraz i dźwięk — tryb „tylko dźwięk” można przełączać w trakcie.
                var info = await YtDlp.ResolveAsync(Video.Id, maxH, false, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Stream = info;
                bool hls = (info.VideoUrl ?? info.AudioUrl ?? "").Contains("m3u8", StringComparison.Ordinal);
                if (info.IsLive || hls)
                {
                    // Transmisja na żywo: HLS (MPEG-TS + packed AAC).
                    IsLive = true;
                    Duration = TimeSpan.Zero;
                    _pendingPosition = TimeSpan.Zero;
                    SetState(PlayerState.Buffering, "Łączenie z transmisją na żywo…");
                    byte[]? asc = null;
                    int rate = 0, ch = 0;
                    if (info.AudioUrl != null)
                    {
                        var probe = new HlsAudioSource(info.AudioUrl, new HlsShared());
                        await Task.Run(() => probe.Probe(ct), ct).ConfigureAwait(false);
                        (asc, rate, ch) = (probe.AudioConfig, probe.SampleRate, probe.Channels);
                    }
                    string? vUrl = info.VideoUrl, aUrl = info.AudioUrl;
                    int w = info.Width, h = info.Height;
                    _liveFactory = () =>
                    {
                        var shared = new HlsShared();
                        IMediaSource? v = vUrl != null ? new HlsVideoSource(vUrl, shared, w, h) : null;
                        IMediaSource? a = aUrl != null ? new HlsAudioSource(aUrl, shared, asc, rate, ch) : null;
                        return (v, a);
                    };
                    (_videoDemux, _audioDemux) = _liveFactory();
                }
                else
                {
                    if (info.Duration > 0) Duration = TimeSpan.FromSeconds(info.Duration);
                    SetState(PlayerState.Buffering, "Otwieranie strumieni…");
                    var vTask = info.VideoUrl != null
                        ? Task.Run(() => Mp4Demuxer.Open(info.VideoUrl, "vide", ct), ct) : Task.FromResult<Mp4Demuxer?>(null)!;
                    var aTask = info.AudioUrl != null
                        ? Task.Run(() => Mp4Demuxer.Open(info.AudioUrl, "soun", ct), ct) : Task.FromResult<Mp4Demuxer?>(null)!;
                    var vd = await vTask.ConfigureAwait(false);
                    var ad = await aTask.ConfigureAwait(false);
                    _videoDemux = vd != null ? new Mp4Source(vd) : null;
                    _audioDemux = ad != null ? new Mp4Source(ad) : null;
                }
                if (_videoDemux?.Codec is { } vc && vc is not ("avc1" or "avc3"))
                    throw new NotSupportedException($"Nieobsługiwany kodek wideo: {vc}");
                if (_audioDemux?.Codec is { } ac && ac != "mp4a")
                    throw new NotSupportedException($"Nieobsługiwany kodek audio: {ac}");
                if (Duration <= TimeSpan.Zero && !IsLive) Duration = _videoDemux?.Duration ?? _audioDemux?.Duration ?? TimeSpan.Zero;

                lock (_lock)
                {
                    if (_disposed) return;
                    StartSessionLocked(_pendingPosition);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }, ct);
    }

    private int AutoHeight(Rect area)
    {
        if (Options.Mode == RenderMode.HalfBlock) return 360;
        double pxH = Math.Min(area.H * Options.CellHeight, area.W * Options.CellWidth * 9.0 / 16.0) * Options.Scale;
        return pxH <= 380 ? 360 : pxH <= 520 ? 480 : pxH <= 760 ? 720 : 1080;
    }

    public bool HasVideo => _videoDemux != null;
    public bool EverStarted { get; private set; }

    private void Fail(Exception ex)
    {
        Storage.Log.Error($"Odtwarzanie {Video.Id}", ex);
        Error = ex is MediaFoundationException or InvalidOperationException or NotSupportedException or IOException or HttpRequestException
            ? ex.Message
            : ex.GetType().Name + ": " + ex.Message;
        SetState(PlayerState.Error, Error);
    }

    private void SetState(PlayerState s, string text)
    {
        State = s;
        StatusText = text;
        Changed?.Invoke();
    }

    private void StartSessionLocked(TimeSpan position)
    {
        var old = _session;
        _session = null;
        if (old != null) Task.Run(old.Dispose);

        if (Duration > TimeSpan.Zero && position >= Duration) position = Duration - TimeSpan.FromSeconds(1);
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        _pendingPosition = position;
        if (IsLive && _liveFactory != null)
        {
            // Każda sesja transmisji zaczyna od bieżącej krawędzi (nowe źródła ze wspólną osią czasu).
            (_videoDemux, _audioDemux) = _liveFactory();
            position = TimeSpan.Zero;
        }

        if (_videoDemux == null && _audioDemux == null) return;
        SetState(PlayerState.Buffering, "Buforowanie…");
        var s = new Session(this, position, Paused);
        _session = s;
        s.Start();
    }

    internal void OnSessionStarted(Session s)
    {
        if (s != _session) return;
        EverStarted = true;
        SetState(Paused ? PlayerState.Paused : PlayerState.Playing, "");
    }

    internal void OnSessionEnded(Session s)
    {
        if (s != _session) return;
        SetState(PlayerState.Ended, "Koniec");
        Ended?.Invoke();
    }

    internal void OnSessionError(Session s, Exception ex)
    {
        if (s != _session) return;
        Fail(ex);
    }

    internal void RaiseChanged() => Changed?.Invoke();

    public void TogglePause()
    {
        if (State is PlayerState.Ended)
        {
            Seek(TimeSpan.Zero);
            Paused = false;
            return;
        }
        Paused = !Paused;
        _session?.SetPaused(Paused);
        if (State is PlayerState.Playing or PlayerState.Paused)
            SetState(Paused ? PlayerState.Paused : PlayerState.Playing, "");
        else Changed?.Invoke();
    }

    /// <summary>Przewija (z opóźnieniem, by kolejne naciśnięcia klawisza sumowały się).</summary>
    /// <summary>Transmisja na żywo: powrót do bieżącej krawędzi.</summary>
    public void JumpToLive()
    {
        if (!IsLive) return;
        lock (_lock) ScheduleRestartLocked(50);
    }

    public void Seek(TimeSpan target)
    {
        if (IsLive) { JumpToLive(); return; }
        if (Duration > TimeSpan.Zero && target > Duration - TimeSpan.FromSeconds(0.5)) target = Duration - TimeSpan.FromSeconds(0.5);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        lock (_lock)
        {
            _pendingPosition = target;
            ScheduleRestartLocked(250);
        }
        Changed?.Invoke();
    }

    public void SeekRelative(double seconds) => Seek(Position + TimeSpan.FromSeconds(seconds));

    private void ScheduleRestartLocked(int delayMs)
    {
        _restartTimer?.Dispose();
        _restartTimer = new Timer(_ =>
        {
            try
            {
                lock (_lock)
                {
                    _restartTimer?.Dispose();
                    _restartTimer = null;
                    if (_disposed || (_videoDemux == null && _audioDemux == null)) return;
                    if (State == PlayerState.Ended) Paused = false;
                    StartSessionLocked(_pendingPosition);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }, null, delayMs, Timeout.Infinite);
    }

    /// <summary>Zmiana obszaru wideo (rozmiar terminala, tryb pełnoekranowy) — restart sesji z nowym rozmiarem.</summary>
    public void SetArea(Rect area)
    {
        if (area == _area) return;
        _area = area;
        lock (_lock)
        {
            if (_session == null && _restartTimer == null) return;
            if (_restartTimer == null) _pendingPosition = Position;
            ScheduleRestartLocked(350);
        }
    }

    /// <summary>Ponowne uruchomienie po zmianie opcji (tryb renderowania, rozmiar, paleta).</summary>
    public void ApplyOptions()
    {
        lock (_lock)
        {
            if (_session == null && _restartTimer == null) return;
            if (_restartTimer == null) _pendingPosition = Position;
            ScheduleRestartLocked(50);
        }
    }

    public void RedrawLastFrame() => _session?.RedrawLast();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _restartTimer?.Dispose();
            _restartTimer = null;
            _cts.Cancel();
            var s = _session;
            _session = null;
            s?.Dispose();
        }
    }

    // =====================================================================================

    internal sealed class Session : IDisposable
    {
        private readonly Player _p;
        private readonly TimeSpan _start;
        private readonly CancellationTokenSource _cts = new();
        private readonly BlockingCollection<EncodedFrame> _frames = new(3);
        private byte[][]? _onScreen; // sygnatury pasków aktualnie widocznych na ekranie
        private readonly ByteBuilder _presentBuf = new(1 << 18);
        private readonly object _presentLock = new();
        public long BytesWritten;
        private readonly ManualResetEventSlim _videoReady = new(false);
        private readonly ManualResetEventSlim _audioReady = new(false);
        private readonly Stopwatch _wall = new();
        private Thread? _videoThread, _audioThread, _presentThread;
        private volatile WaveOut? _wave;
        private TimeSpan _audioBase;
        private bool _audioBaseSet;
        private volatile bool _started;
        private volatile bool _paused;
        private volatile bool _audioEnded;
        private TimeSpan _audioEndClock;
        private readonly Stopwatch _afterAudio = new();
        private EncodedFrame? _lastFrame;
        private readonly ImageEncoder _encoder = new();
        private ImageLayout _layout;
        private readonly Stopwatch _fpsWatch = new();
        private int _fpsCount;
        private volatile bool _disposed;

        public int Presented;
        public int Dropped;
        public double MeasuredFps;
        public int PixelWidth => _layout.PixelWidth;
        public int PixelHeight => _layout.PixelHeight;

        public Session(Player p, TimeSpan start, bool paused)
        {
            _p = p;
            _start = start;
            _paused = paused;
            var o = p.Options;
            _encoder.Mode = o.Mode;
            _encoder.CellWidth = o.CellWidth;
            _encoder.CellHeight = o.CellHeight;
            _encoder.AdaptivePalette = o.AdaptivePalette;
            _encoder.Dither = o.Dither;
            _encoder.MaxColors = o.Colors;
        }

        public TimeSpan Clock
        {
            get
            {
                if (!_started) return _start;
                var w = _wave;
                if (w != null)
                {
                    long played = w.PlayedFrames;
                    var t = _audioBase + TimeSpan.FromSeconds(played / (double)w.SampleRate);
                    if (_audioEnded && played >= w.SubmittedFrames - w.SampleRate / 50)
                    {
                        // Dźwięk się skończył — dalej liczymy czas zegarem ściennym.
                        if (!_afterAudio.IsRunning && !_paused) { _audioEndClock = t; _afterAudio.Restart(); }
                        return _audioEndClock + _afterAudio.Elapsed;
                    }
                    return t;
                }
                return (_wallBase ?? _start) + _wall.Elapsed;
            }
        }

        private TimeSpan? _wallBase;
        private TimeSpan _origin;

        /// <summary>Czas oglądania transmisji na żywo od dołączenia.</summary>
        public TimeSpan LiveElapsed => _started ? Clock - _origin : TimeSpan.Zero;

        public void Start()
        {
            var vd = _p.Options.AudioOnly ? null : _p._videoDemux;
            var ad = _p._audioDemux;
            if (vd != null)
            {
                var t = vd;
                var area = _p._area;
                // Limit szerokości w pikselach (duże okna = ogromne klatki Sixel).
                if (_p.Options.Mode == RenderMode.Sixel && _p.Options.MaxWidthPx > 0)
                {
                    int maxCols = Math.Max(10, _p.Options.MaxWidthPx / Math.Max(1, _p.Options.CellWidth));
                    if (area.W > maxCols) area = new Rect(area.X + (area.W - maxCols) / 2, area.Y, maxCols, area.H);
                }
                _layout = _encoder.Layout(t.Width > 0 ? t.Width : 1280, t.Height > 0 ? t.Height : 720, area, _p.Options.Scale);
                _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "Video", Priority = ThreadPriority.AboveNormal };
                _videoThread.Start(vd);
            }
            else _videoReady.Set();

            if (ad != null)
            {
                _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "Audio", Priority = ThreadPriority.Highest };
                _audioThread.Start(ad);
            }
            else { _audioReady.Set(); _audioEnded = true; }

            _presentThread = new Thread(PresentLoop) { IsBackground = true, Name = "Present" };
            _presentThread.Start();
        }

        public void ApplyVolume() => _wave?.SetVolume(_p.EffectiveVolume);

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (!_started) return;
            if (paused)
            {
                _wave?.Pause();
                _wall.Stop();
                _afterAudio.Stop();
            }
            else
            {
                _wave?.Resume();
                if (_wave == null) _wall.Start();
                if (_afterAudio.Elapsed > TimeSpan.Zero) _afterAudio.Start();
            }
        }

        public void RedrawLast()
        {
            var f = _lastFrame;
            if (f != null && !_disposed) Present(f, force: true);
        }

        /// <summary>Wysyła klatkę — tylko paski, które różnią się od tego, co jest na ekranie (chyba że force).</summary>
        private void Present(EncodedFrame f, bool force)
        {
            lock (_presentLock)
            {
                if (_onScreen == null || _onScreen.Length != f.Strips.Length) { _onScreen = new byte[f.Strips.Length][]; force = true; }
                var b = _presentBuf;
                b.Clear();
                for (int s = 0; s < f.Strips.Length; s++)
                {
                    if (!force && ImageEncoder.SimilarSignature(_onScreen[s], f.Signatures[s])) continue;
                    b.Append(f.Strips[s]);
                    _onScreen[s] = f.Signatures[s];
                }
                if (b.Length == 0) return;
                BytesWritten += b.Length;
                if (_p.Options.SyncOutput)
                    _p._term.WriteAtomic("\x1b[?2026h"u8, b.Span, "\x1b[?2026l"u8);
                else
                    _p._term.Write(b.Span);
            }
        }

        // ---------------- Wideo ----------------

        private unsafe void VideoLoop(object? state)
        {
            var demux = (IMediaSource)state!;
            var ct = _cts.Token;
            try
            {
                MF.EnsureStarted();
                var track = demux;
                using var dec = new H264Decoder(track.AnnexB ? null : track.AvcConfig ?? throw new InvalidDataException("Brak konfiguracji AVC"), track.Width, track.Height);
                uint[] full = [];
                var scaled = new uint[Math.Max(1, _layout.PixelWidth * _layout.PixelHeight)];
                TimeSpan minInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _p.Options.MaxFps)) - TimeSpan.FromMilliseconds(2);
                TimeSpan? lastTs = null;
                TimeSpan frameDur = TimeSpan.FromSeconds(1.0 / 30);

                dec.OnFrame = (time, y, uv, stride) =>
                {
                    var ts = TimeSpan.FromTicks(time);
                    if (ts < _start - TimeSpan.FromMilliseconds(20)) return;            // dekodowanie do punktu przewinięcia
                    if (lastTs is { } l && ts - l < minInterval && ts > l) return;       // limit FPS
                    if (_started && ts < Clock - frameDur * 2) { Interlocked.Increment(ref Dropped); return; } // spóźniona
                    if (lastTs is { } l2 && ts > l2) frameDur = ts - l2 < TimeSpan.FromSeconds(0.2) ? ts - l2 : frameDur;
                    lastTs = ts;

                    int dw = dec.DisplayWidth, dh = dec.DisplayHeight;
                    if (full.Length < dw * dh) full = new uint[dw * dh];
                    ColorConvert.Nv12ToArgb(y, uv, stride, dec.CropX, dec.CropY, dw, dh, full, true);
                    fixed (uint* f = full)
                        Scaler.Resize(f, dw, dh, dw, scaled, _layout.PixelWidth, _layout.PixelHeight);
                    var frame = _encoder.EncodeVideoFrame(scaled, _layout.PixelWidth, _layout.PixelHeight, _layout.Cells);
                    frame.Time = ts;
                    _frames.Add(frame, ct);
                    _videoReady.Set();
                };

                foreach (var s in demux.Read(_start, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    dec.Decode(s);
                }
                dec.EndOfStream();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) _p.OnSessionError(this, ex);
            }
            finally
            {
                _videoReady.Set();
                try { _frames.CompleteAdding(); } catch { }
            }
        }

        // ---------------- Audio ----------------

        private void AudioLoop(object? state)
        {
            var demux = (IMediaSource)state!;
            var ct = _cts.Token;
            try
            {
                MF.EnsureStarted();
                var track = demux;
                using var dec = new AacDecoder(track.AudioConfig, track.SampleRate, track.Channels);
                long startTicks = _start.Ticks;

                dec.OnPcm = (pcm, time) =>
                {
                    int rate = dec.SampleRate, ch = dec.Channels;
                    if (rate <= 0 || ch <= 0) return;
                    int block = ch * 2;
                    long frames = pcm.Length / block;
                    long endTicks = time + frames * TimeSpan.TicksPerSecond / rate;
                    if (endTicks <= startTicks) return;
                    int skip = 0;
                    if (time < startTicks)
                        skip = (int)((startTicks - time) * rate / TimeSpan.TicksPerSecond) * block;
                    if (_wave == null)
                    {
                        _wave = new WaveOut(rate, ch, bufferCount: 8);
                        _wave.SetVolume(_p.EffectiveVolume);
                        _wave.Pause();
                        _audioBase = TimeSpan.FromTicks(Math.Max(startTicks, time));
                        _audioBaseSet = true;
                    }
                    _wave.Write(pcm[Math.Min(skip, pcm.Length)..], 1f, ct); // głośność ustawia SetVolume
                    if (!_audioReady.IsSet && _wave.SubmittedFrames >= rate * 3 / 10) _audioReady.Set();
                };

                foreach (var s in demux.Read(_start, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    dec.Decode(s);
                }
                dec.EndOfStream();
                _wave?.Flush();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    // Brak dźwięku nie przerywa obrazu — przechodzimy na zegar ścienny.
                    if (_p._videoDemux == null) _p.OnSessionError(this, ex);
                }
            }
            finally
            {
                _audioEnded = true;
                _audioReady.Set();
                _ = _audioBaseSet;
            }
        }

        // ---------------- Prezentacja ----------------

        private void PresentLoop()
        {
            var ct = _cts.Token;
            try
            {
                // Czekamy, aż wideo ma pierwszą klatkę, a audio zbuforowane ~0,3 s.
                WaitHandle.WaitAll([_videoReady.WaitHandle, _audioReady.WaitHandle]);
                if (ct.IsCancellationRequested) return;

                _started = true;
                _origin = _wave != null ? _audioBase : _start;
                if (!_paused)
                {
                    _wave?.Resume();
                    if (_wave == null) _wall.Start();
                }
                _fpsWatch.Start();
                _p.OnSessionStarted(this);

                bool first = true;
                foreach (var frame in _frames.GetConsumingEnumerable(ct))
                {
                    if (first && _wave == null && frame.Time > _start + TimeSpan.FromSeconds(1))
                    {
                        // Bez dźwięku (np. transmisja) zegar startuje od czasu pierwszej klatki.
                        _wallBase = frame.Time;
                        _origin = frame.Time;
                        _wall.Restart();
                        if (_paused) _wall.Stop();
                    }
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var diff = frame.Time - Clock;
                        if (diff <= TimeSpan.FromMilliseconds(4)) break;
                        // Wstrzymane odtwarzanie: pierwsza klatka po przewinięciu i tak ma się pokazać.
                        if (first && _paused) break;
                        Thread.Sleep(Math.Clamp((int)diff.TotalMilliseconds - 2, 1, 15));
                    }
                    var late = Clock - frame.Time;
                    if (!first && late > TimeSpan.FromMilliseconds(120) && _frames.Count > 0)
                    {
                        Interlocked.Increment(ref Dropped);
                        continue;
                    }
                    Present(frame, force: first);
                    first = false;
                    _lastFrame = frame;
                    Presented++;
                    _fpsCount++;
                    if (_fpsWatch.ElapsedMilliseconds >= 1000)
                    {
                        MeasuredFps = _fpsCount * 1000.0 / _fpsWatch.ElapsedMilliseconds;
                        _fpsCount = 0;
                        _fpsWatch.Restart();
                    }
                }

                // Koniec obrazu — czekamy na koniec dźwięku.
                while (!ct.IsCancellationRequested)
                {
                    var w = _wave;
                    if (_audioEnded && (w == null || w.PlayedFrames >= w.SubmittedFrames - w.SampleRate / 20)) break;
                    if (_p.Duration > TimeSpan.Zero && Clock >= _p.Duration + TimeSpan.FromSeconds(1)) break;
                    Thread.Sleep(50);
                }
                if (!ct.IsCancellationRequested) _p.OnSessionEnded(this);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) _p.OnSessionError(this, ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _videoReady.Set();
            _audioReady.Set();
            _videoThread?.Join(3000);
            _audioThread?.Join(3000);
            _presentThread?.Join(3000);
            _wave?.Dispose();
            _wave = null;
            _frames.Dispose();
        }
    }
}
