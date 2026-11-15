using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using YoutubeTerminal.Storage;

namespace YoutubeTerminal.Playback;

/// <summary>
/// Odszukanie ffmpeg: najpierw zainstalowany w systemie, a dopiero gdy go nie ma — wbudowany w program
/// (wypakowywany przy pierwszej potrzebie do folderu danych aplikacji).
/// </summary>
internal static partial class Ffmpeg
{
    private static readonly object Lock = new();
    private static string? _path;
    private static bool _searched;
    private static readonly Dictionary<string, int> Versions = new();

    public static string ExeName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public static string? Find()
    {
        lock (Lock)
        {
            if (!_searched)
            {
                _path = Search();
                _searched = true;
            }
            return _path;
        }
    }

    public static string Require() => Find() ?? throw new InvalidOperationException(L.T(
        "Do odtwarzania potrzebny jest ffmpeg — zainstaluj go (np. sudo apt install ffmpeg albo brew install ffmpeg).",
        "Playback needs ffmpeg — please install it (e.g. sudo apt install ffmpeg or brew install ffmpeg)."));

    /// <summary>Czy ffmpeg pochodzi z systemu (a nie z kopii wbudowanej w program).</summary>
    public static bool IsSystem => Find() is { } p && !p.StartsWith(AppPaths.BinDir, StringComparison.Ordinal);

    private static string? Search()
    {
        if (Environment.GetEnvironmentVariable("YOUTUBETERMINAL_FFMPEG") is { Length: > 0 } env && File.Exists(env)) return env;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        // Programy uruchomione spoza powłoki logowania (np. z menedżera plików) mogą nie mieć Homebrew w PATH.
        if (!OperatingSystem.IsWindows()) dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin"]);
        foreach (var dir in dirs)
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), ExeName);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        try { return Installer.ExtractFfmpeg(); }
        catch (Exception ex)
        {
            Log.Error("ffmpeg", ex);
            return null;
        }
    }

    /// <summary>Główny numer wersji (0, gdy nieznany — np. kompilacja z gałęzi głównej).</summary>
    public static int MajorVersion(string exe)
    {
        lock (Versions)
            if (Versions.TryGetValue(exe, out int v)) return v;
        int major = 0;
        try
        {
            var psi = StartInfo(exe);
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            string first = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(3000);
            var m = VersionRegex().Match(first);
            if (m.Success) major = int.Parse(m.Groups[1].Value);
        }
        catch { }
        lock (Versions) Versions[exe] = major;
        return major;
    }

    [GeneratedRegex(@"version n?(\d+)\.")]
    private static partial Regex VersionRegex();

    /// <summary>Każda zdekodowana klatka dokładnie raz (bez powielania i gubienia do stałego FPS).</summary>
    public static string[] PassthroughArgs(string exe) =>
        MajorVersion(exe) is > 0 and < 5 ? ["-vsync", "0"] : ["-fps_mode", "passthrough"];

    public static ProcessStartInfo StartInfo(string exe) => new(exe)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    public static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    internal static bool ReadExactly(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>Wspólna obsługa procesu ffmpeg: stdin = dane skompresowane, stdout = dane zdekodowane.</summary>
internal abstract class FfmpegProcess : IDisposable
{
    protected readonly Process Proc;
    protected readonly Stream Input;
    private readonly StringBuilder _stderr = new();
    private readonly Thread _reader;
    private volatile bool _aborted;
    protected Exception? ReaderError;

    protected FfmpegProcess(ProcessStartInfo psi, string threadName)
    {
        Proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg");
        Input = Proc.StandardInput.BaseStream;
        Proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr) if (_stderr.Length < 4000) _stderr.AppendLine(e.Data);
        };
        Proc.BeginErrorReadLine();
        _reader = new Thread(() =>
        {
            try { ReadOutput(Proc.StandardOutput.BaseStream); }
            catch (Exception ex) when (!_aborted)
            {
                // Odbiorca danych zawiódł — zatrzymujemy ffmpeg, żeby zapis na jego wejście się nie zablokował.
                ReaderError = ex;
                try { Proc.Kill(); } catch { }
            }
            catch { }
        }) { IsBackground = true, Name = threadName };
    }

    /// <summary>Uruchamia odczyt wyjścia — wywoływane na końcu konstruktora klasy pochodnej (po ustawieniu jej pól).</summary>
    protected void StartReading() => _reader.Start();

    protected abstract void ReadOutput(Stream stdout);

    protected string ErrorText
    {
        get
        {
            lock (_stderr)
            {
                var s = _stderr.ToString().Trim();
                return s.Length > 0 ? s.Split('\n')[^1].Trim() : L.T("proces zakończył się nieoczekiwanie", "the process exited unexpectedly");
            }
        }
    }

    protected void WriteInput(ReadOnlySpan<byte> data)
    {
        try
        {
            Input.Write(data);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (_aborted) throw new OperationCanceledException();
            if (ReaderError != null) throw new IOException(ReaderError.Message, ReaderError);
            throw new IOException("ffmpeg: " + ErrorText, ex);
        }
    }

    public void EndOfStream()
    {
        try { Input.Flush(); Input.Close(); } catch { }
        _reader.Join();
        if (ReaderError != null && !_aborted) throw new IOException("ffmpeg: " + ReaderError.Message, ReaderError);
    }

    /// <summary>Przerywa dekodowanie (np. przy przewijaniu) — odblokowuje wątki czekające na proces.</summary>
    public void Abort()
    {
        _aborted = true;
        try { if (!Proc.HasExited) Proc.Kill(); } catch { }
    }

    public void Dispose()
    {
        Abort();
        try { Input.Dispose(); } catch { }
        if ((_reader.ThreadState & System.Threading.ThreadState.Unstarted) == 0) _reader.Join(2000);
        try { Proc.WaitForExit(2000); } catch { }
        Proc.Dispose();
    }
}

/// <summary>
/// H.264 przez ffmpeg: Annex B na wejściu, klatki BGRA w rozmiarze wyjściowym na wyjściu.
/// Wyjście nie niesie znaczników czasu — klatki wychodzą w kolejności prezentacji, więc każdej
/// przypisujemy najmniejszy czas spośród próbek przekazanych, a jeszcze niezwróconych.
/// </summary>
internal sealed class FfmpegVideoDecoder : FfmpegProcess, IVideoDecoder
{
    private readonly H264Bitstream _bits;
    private readonly PriorityQueue<long, long> _pending = new();
    private readonly int _w, _h;
    private readonly VideoFrameFilter _filter;
    private readonly VideoFrameSink _sink;
    private long _lastTime;

    public FfmpegVideoDecoder(string exe, IMediaSource track, int width, int height, VideoFrameFilter filter, VideoFrameSink sink)
        : base(CreateStartInfo(exe, width, height, track), "ffmpeg-video")
    {
        _bits = new H264Bitstream(track.AnnexB ? null : track.AvcConfig);
        _w = Math.Max(1, width);
        _h = Math.Max(1, height);
        _filter = filter;
        _sink = sink;
        StartReading();
    }

    internal static ProcessStartInfo CreateStartInfo(string exe, int width, int height, IMediaSource? track = null)
    {
        // Sprawdzenie przed uruchomieniem procesu (wyjątek w konstruktorze zostawiłby działający ffmpeg).
        if (track is { AnnexB: false, AvcConfig: null })
            throw new InvalidDataException(L.T("Brak konfiguracji AVC", "Missing AVC configuration"));
        var psi = Ffmpeg.StartInfo(exe);
        Ffmpeg.AddArgs(psi, "-hide_banner", "-loglevel", "error",
            "-probesize", "65536", "-analyzeduration", "0",
            "-f", "h264", "-i", "pipe:0",
            "-an", "-sn",
            "-vf", $"scale={Math.Max(1, width)}:{Math.Max(1, height)}:flags=bilinear",
            "-pix_fmt", "bgra");
        Ffmpeg.AddArgs(psi, Ffmpeg.PassthroughArgs(exe));
        Ffmpeg.AddArgs(psi, "-f", "rawvideo", "pipe:1");
        return psi;
    }

    public void Decode(in MediaSample sample)
    {
        var data = _bits.ToAnnexB(sample);
        if (data.IsEmpty) return;
        lock (_pending) _pending.Enqueue(sample.Time, sample.Time);
        WriteInput(data);
    }

    protected override void ReadOutput(Stream stdout)
    {
        int frameBytes = _w * _h * 4;
        var bytes = new byte[frameBytes];
        var pixels = new uint[_w * _h];
        while (Ffmpeg.ReadExactly(stdout, bytes, frameBytes))
        {
            long t;
            lock (_pending)
                t = _pending.TryDequeue(out var x, out _) ? x : _lastTime + 1;
            _lastTime = t;
            var ts = TimeSpan.FromTicks(t);
            if (!_filter(ts)) continue;
            Buffer.BlockCopy(bytes, 0, pixels, 0, frameBytes);
            _sink(ts, pixels);
        }
    }
}

/// <summary>AAC przez ffmpeg: ramki z nagłówkami ADTS na wejściu, PCM 16-bit 48 kHz stereo na wyjściu.</summary>
internal sealed class FfmpegAudioDecoder : FfmpegProcess, IAudioDecoder
{
    public const int OutputRate = 48000;
    public const int OutputChannels = 2;

    private readonly (int Profile, int FreqIndex, int Channels) _adts;
    private readonly PcmSink _sink;
    private readonly byte[] _frame = new byte[8192];
    private long _firstTime = -1;

    public FfmpegAudioDecoder(string exe, IMediaSource track, PcmSink sink)
        : base(CreateStartInfo(exe), "ffmpeg-audio")
    {
        _adts = Adts.FromConfig(track.AudioConfig, track.SampleRate, track.Channels);
        _sink = sink;
        StartReading();
    }

    internal static ProcessStartInfo CreateStartInfo(string exe)
    {
        var psi = Ffmpeg.StartInfo(exe);
        Ffmpeg.AddArgs(psi, "-hide_banner", "-loglevel", "error",
            "-probesize", "4096", "-analyzeduration", "0",
            "-f", "aac", "-i", "pipe:0",
            "-vn", "-ar", OutputRate.ToString(), "-ac", OutputChannels.ToString(),
            "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1");
        return psi;
    }

    public void Decode(in MediaSample sample)
    {
        var payload = sample.Data;
        if (payload.IsEmpty || payload.Length + 7 > 0x1FFF) return;
        if (Interlocked.Read(ref _firstTime) < 0) Interlocked.Exchange(ref _firstTime, sample.Time);
        Adts.WriteHeader(_frame, _adts.Profile, _adts.FreqIndex, _adts.Channels, payload.Length);
        payload.CopyTo(_frame.AsSpan(7));
        WriteInput(_frame.AsSpan(0, payload.Length + 7));
    }

    protected override void ReadOutput(Stream stdout)
    {
        const int block = OutputChannels * 2;
        var buf = new byte[4096 * block];
        long frames = 0;
        int carry = 0;
        while (true)
        {
            int n = stdout.Read(buf, carry, buf.Length - carry);
            if (n <= 0) break;
            int total = carry + n;
            int usable = total - total % block;
            long first = Interlocked.Read(ref _firstTime);
            long time = Math.Max(0, first) + frames * TimeSpan.TicksPerSecond / OutputRate;
            _sink(buf.AsSpan(0, usable), time, OutputRate, OutputChannels);
            frames += usable / block;
            carry = total - usable;
            if (carry > 0) Buffer.BlockCopy(buf, usable, buf, 0, carry);
        }
    }
}
