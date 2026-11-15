using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using YoutubeTerminal.Platform;
using YoutubeTerminal.Playback;
using YoutubeTerminal.Tests.Helpers;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Tests;

public class H264BitstreamTests
{
    // avcC: wersja 1, profil 0x64, NAL długości 4 B (0xFF → 3+1), 1 SPS (3 B), 1 PPS (2 B)
    private static readonly byte[] AvcC = [1, 0x64, 0, 0x1F, 0xFF, 0xE1, 0, 3, 0x67, 0xAA, 0xBB, 1, 0, 2, 0x68, 0xCC];

    [Fact]
    public void ParseAvcC_ExtractsSpsAndPpsAsAnnexB()
    {
        var (ps, nalLen) = H264Bitstream.ParseAvcC(AvcC);
        Assert.Equal(4, nalLen);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0xAA, 0xBB, 0, 0, 0, 1, 0x68, 0xCC }, ps);
    }

    [Fact]
    public void KeyFrame_GetsParameterSetsAndStartCodes()
    {
        var bits = new H264Bitstream(AvcC);
        byte[] avcc = [0, 0, 0, 2, 0x65, 0x11, 0, 0, 0, 1, 0x06];
        var s = new MediaSample(avcc, 0, avcc.Length, 0, 0, isKey: true);
        var annexB = bits.ToAnnexB(s).ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0xAA, 0xBB, 0, 0, 0, 1, 0x68, 0xCC, 0, 0, 0, 1, 0x65, 0x11, 0, 0, 0, 1, 0x06 }, annexB);
    }

    [Fact]
    public void DeltaFrame_HasNoParameterSets()
    {
        var bits = new H264Bitstream(AvcC);
        byte[] avcc = [0, 0, 0, 2, 0x41, 0x22];
        var annexB = bits.ToAnnexB(new MediaSample(avcc, 0, avcc.Length, 0, 0, isKey: false)).ToArray();
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x41, 0x22 }, annexB);
    }

    [Fact]
    public void AnnexBInput_IsPassedThrough()
    {
        var bits = new H264Bitstream(null);
        byte[] data = [0, 0, 1, 0x65, 0x01];
        Assert.Equal(data, bits.ToAnnexB(new MediaSample(data, 0, data.Length, 0, 0, true)).ToArray());
    }
}

public class AdtsTests
{
    [Fact]
    public void Header_EncodesLcStereo44100()
    {
        // ASC AAC-LC, 44,1 kHz (indeks 4), stereo: 00010 0100 0010 000 → 0x12 0x10
        var (profile, freq, ch) = Adts.FromConfig([0x12, 0x10], 0, 0);
        Assert.Equal((2, 4, 2), (profile, freq, ch));
        var h = new byte[7];
        Adts.WriteHeader(h, profile, freq, ch, 100);
        Assert.Equal(0xFF, h[0]);
        Assert.Equal(0xF1, h[1]);
        Assert.Equal(1, h[2] >> 6);                 // profil LC zapisany jako 1
        Assert.Equal(4, (h[2] >> 2) & 0xF);         // częstotliwość
        int chan = ((h[2] & 1) << 2) | (h[3] >> 6);
        Assert.Equal(2, chan);
        int len = ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5);
        Assert.Equal(107, len);                     // nagłówek + dane
    }

    [Fact]
    public void FromConfig_WithoutAscUsesParameters()
    {
        Assert.Equal((2, 3, 1), Adts.FromConfig(null, 48000, 1));
    }
}

/// <summary>Atrapa warstwy systemowej terminala: zapisuje wyjście i podaje zadane wejście.</summary>
internal sealed class FakeConsole : IConsoleBackend
{
    public readonly MemoryStream Output = new();
    public readonly ConcurrentQueue<string> Input = new();
    public bool Raw;
    public int Columns = 100, Rows = 30;
    public bool ResizePending;

    public void Init() => Raw = true;
    public void Restore() => Raw = false;

    public bool TryGetSize(out int columns, out int rows)
    {
        columns = Columns;
        rows = Rows;
        return true;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (Output) Output.Write(data);
    }

    public bool Read(StringBuilder into, int timeoutMs, out bool resized)
    {
        resized = ResizePending;
        ResizePending = false;
        if (Input.TryDequeue(out var s)) { into.Append(s); return true; }
        Thread.Sleep(Math.Min(timeoutMs, 5));
        return resized;
    }

    public string Text { get { lock (Output) return Encoding.UTF8.GetString(Output.ToArray()); } }

    public void Dispose() { }
}

public class TerminalBackendTests
{
    [Fact]
    public void Init_EntersRawModeAndAlternateScreen_DisposeRestores()
    {
        var fake = new FakeConsole();
        var term = new Terminal(fake);
        term.Init();
        Assert.True(fake.Raw);
        Assert.Contains("\x1b[?1049h", fake.Text);
        Assert.Equal(100, term.Width);
        Assert.Equal(30, term.Height);

        term.Dispose();
        Assert.False(fake.Raw);
        Assert.EndsWith("\x1b[?1049l", fake.Text);
    }

    [Fact]
    public void UpdateSize_ReportsChanges()
    {
        var fake = new FakeConsole();
        var term = new Terminal(fake);
        term.Init();
        Assert.False(term.UpdateSize());
        fake.Columns = 120;
        Assert.True(term.UpdateSize());
        Assert.Equal(120, term.Width);
        term.Dispose();
    }

    [Fact]
    public void InputReader_TurnsBackendCharactersIntoKeys()
    {
        var fake = new FakeConsole();
        var term = new Terminal(fake);
        var queue = new BlockingCollection<InputEvent>();
        using var reader = new InputReader(term, queue, () => { });
        // Strzałka w górę (CSI), Enter (CR — tak przychodzi w trybie surowym Unix), litera i zmiana rozmiaru.
        fake.Input.Enqueue("\x1b[A");
        fake.Input.Enqueue("\r");
        fake.Input.Enqueue("ż");
        reader.Start();

        var keys = new List<KeyEvent>();
        var sw = Stopwatch.StartNew();
        while (keys.Count < 3 && sw.ElapsedMilliseconds < 3000)
            if (queue.TryTake(out var e, 50) && e is KeyEvent k) keys.Add(k);
        fake.ResizePending = true;
        bool resized = false;
        while (!resized && sw.ElapsedMilliseconds < 4000)
            if (queue.TryTake(out var e, 50) && e is ResizeEvent) resized = true;

        Assert.Equal(Key.Up, keys[0].Key);
        Assert.Equal(Key.Enter, keys[1].Key);
        Assert.Equal('ż', keys[2].Char);
        Assert.True(resized);
    }
}

/// <summary>
/// Dekodowanie przez ffmpeg (ścieżka Linuksa i macOS). Uruchamia się tylko tam, gdzie ffmpeg jest
/// zainstalowany (np. w CI na Linuksie); klip testowy tworzy sam ffmpeg (testsrc + libx264 + AAC).
/// </summary>
public class FfmpegIntegrationTests : IDisposable
{
    private readonly LocalHttpServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ytt-ffmpeg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string? SystemFfmpeg()
    {
        // W CI (YTT_REQUIRE_FFMPEG=1) brak ffmpeg jest błędem, a nie cichym pominięciem testu.
        bool required = Environment.GetEnvironmentVariable("YTT_REQUIRE_FFMPEG") == "1";
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(p)) return p;
        }
        if (required) throw new InvalidOperationException("YTT_REQUIRE_FFMPEG=1, ale nie znaleziono ffmpeg w PATH");
        return null;
    }

    private string? MakeClip(string ffmpeg)
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "clip.mp4");
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-bf", "2", "-g", "25",
            "-c:a", "aac", "-movflags", "+faststart", file,
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 && File.Exists(file) ? file : null;
    }

    [Fact]
    public void VideoAndAudio_DecodeThroughFfmpeg()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg == null) return; // brak ffmpeg na tej maszynie — test sprawdzany w CI
        var clip = MakeClip(ffmpeg);
        if (clip == null)
        {
            if (Environment.GetEnvironmentVariable("YTT_REQUIRE_FFMPEG") == "1") throw new InvalidOperationException("ffmpeg nie utworzył klipu testowego (brak libx264?)");
            return;
        }
        _server.Add("clip.mp4", File.ReadAllBytes(clip));
        // Dekodować może inna kopia ffmpeg niż ta, która utworzyła klip (CI: minimalna, wbudowywana w program).
        string decoder = Environment.GetEnvironmentVariable("YTT_TEST_DECODER_FFMPEG") is { Length: > 0 } d && File.Exists(d) ? d : ffmpeg;

        // Wideo: liczba klatek = liczba próbek, czasy rosnące (mimo klatek B), rozmiar wyjścia jak zadany.
        var vd = new Mp4Source(Mp4Demuxer.Open(_server.Url("clip.mp4"), "vide", CancellationToken.None));
        var times = new List<TimeSpan>();
        int pixelsLength = 0;
        uint centre = 0;
        int samples = 0;
        using (var dec = new FfmpegVideoDecoder(decoder, vd, 64, 36, _ => true, (ts, px) =>
        {
            lock (times) times.Add(ts);
            pixelsLength = px.Length;
            centre = px[18 * 64 + 32];
        }))
        {
            foreach (var s in vd.Read(TimeSpan.Zero, CancellationToken.None)) { dec.Decode(s); samples++; }
            dec.EndOfStream();
        }
        Assert.Equal(50, samples);
        Assert.Equal(samples, times.Count);
        Assert.Equal(64 * 36, pixelsLength);
        for (int i = 1; i < times.Count; i++) Assert.True(times[i] > times[i - 1], $"klatka {i}: {times[i]} ≤ {times[i - 1]}");
        Assert.Equal(0xFFu, centre >> 24); // BGRA → nieprzezroczysty ARGB

        // Dźwięk: ok. 2 s PCM 48 kHz stereo.
        var ad = new Mp4Source(Mp4Demuxer.Open(_server.Url("clip.mp4"), "soun", CancellationToken.None));
        long bytes = 0;
        using (var adec = new FfmpegAudioDecoder(decoder, ad, (pcm, _, rate, ch) =>
        {
            Assert.Equal(48000, rate);
            Assert.Equal(2, ch);
            Interlocked.Add(ref bytes, pcm.Length);
        }))
        {
            foreach (var s in ad.Read(TimeSpan.Zero, CancellationToken.None)) adec.Decode(s);
            adec.EndOfStream();
        }
        double seconds = bytes / (48000.0 * 4);
        Assert.InRange(seconds, 1.8, 2.3);
    }

    [Fact]
    public void Abort_UnblocksDecoder()
    {
        var ffmpeg = SystemFfmpeg();
        if (ffmpeg == null) return;
        var vd = new FakeVideoTrack();
        var dec = new FfmpegVideoDecoder(ffmpeg, vd, 16, 16, _ => true, (_, _) => Thread.Sleep(Timeout.Infinite));
        dec.Abort();
        Assert.ThrowsAny<Exception>(() =>
        {
            for (int i = 0; i < 10000; i++) dec.Decode(new MediaSample(new byte[4096], 0, 4096, i, 1, i == 0));
        });
        dec.Dispose();
    }

    private sealed class FakeVideoTrack : IMediaSource
    {
        public string Codec => "avc1";
        public byte[]? AvcConfig => null;
        public byte[]? AudioConfig => null;
        public bool AnnexB => true;
        public int Width => 16;
        public int Height => 16;
        public int SampleRate => 0;
        public int Channels => 0;
        public TimeSpan Duration => TimeSpan.Zero;
        public bool IsLive => false;
        public IEnumerable<MediaSample> Read(TimeSpan start, CancellationToken ct) => [];
    }
}
