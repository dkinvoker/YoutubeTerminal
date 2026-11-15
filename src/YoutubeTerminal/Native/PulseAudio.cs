using System.Runtime.InteropServices;
using YoutubeTerminal.Playback;

namespace YoutubeTerminal.Native;

/// <summary>
/// Dźwięk w Linuksie przez „simple API” PulseAudio (działa też z PipeWire przez pipewire-pulse).
/// API nie ma pauzy strumienia, więc przy wstrzymaniu dane są gromadzone w programie,
/// a bufor serwera opróżniany — zegar (odtworzone ramki) zatrzymuje się od razu.
/// </summary>
internal sealed unsafe partial class PulseAudioOutput : IAudioOutput
{
    private const string Lib = "libpulse-simple.so.0";
    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_SAMPLE_S16LE = 3;
    /// <summary>Docelowe opóźnienie bufora serwera.</summary>
    private const int LatencyMs = 150;
    /// <summary>Ile dźwięku gromadzimy przy wstrzymaniu, zanim zapis zacznie czekać.</summary>
    private const int MaxHeldSeconds = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        SampleSpec* spec, IntPtr channelMap, BufferAttr* attr, int* error);

    [LibraryImport(Lib)] private static partial int pa_simple_write(IntPtr s, byte* data, nuint bytes, int* error);
    [LibraryImport(Lib)] private static partial int pa_simple_flush(IntPtr s, int* error);
    [LibraryImport(Lib)] private static partial ulong pa_simple_get_latency(IntPtr s, int* error);
    [LibraryImport(Lib)] private static partial void pa_simple_free(IntPtr s);

    private IntPtr _s;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _running = new(true);
    private long _accepted;
    private long _sent;
    private volatile float _volume = 1f;
    private volatile bool _paused;
    private volatile bool _disposed;
    private byte[] _held = new byte[64 * 1024];
    private int _heldLength;
    private byte[] _tmp = new byte[32 * 1024];

    public int SampleRate { get; }
    public int Channels { get; }
    private int Block => Channels * 2;

    public PulseAudioOutput(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var spec = new SampleSpec { Format = PA_SAMPLE_S16LE, Rate = (uint)sampleRate, Channels = (byte)channels };
        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = (uint)(sampleRate * Block * LatencyMs / 1000),
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };
        int err = 0;
        try
        {
            _s = pa_simple_new(null, "YouTube Terminal", PA_STREAM_PLAYBACK, null, "video", &spec, IntPtr.Zero, &attr, &err);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(L.T("Brak biblioteki PulseAudio (libpulse-simple) — odtwarzanie bez dźwięku.",
                "PulseAudio library (libpulse-simple) not found — playing without sound."), ex);
        }
        if (_s == IntPtr.Zero)
            throw new InvalidOperationException(L.T($"Nie można połączyć się z serwerem dźwięku PulseAudio/PipeWire (kod {err}).",
                $"Cannot connect to the PulseAudio/PipeWire sound server (code {err})."));
    }

    public long SubmittedFrames => Interlocked.Read(ref _accepted);

    public long PlayedFrames
    {
        get
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                int e;
                ulong latencyUs = pa_simple_get_latency(_s, &e);
                long latencyFrames = (long)(latencyUs * (ulong)SampleRate / 1_000_000UL);
                return Math.Max(0, Interlocked.Read(ref _sent) - latencyFrames);
            }
        }
    }

    public void SetVolume(float volume) => _volume = Math.Clamp(volume, 0f, 1f);

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct)
    {
        pcm = pcm[..(pcm.Length - pcm.Length % Block)];
        if (pcm.IsEmpty) return;
        while (_paused && !_disposed)
        {
            lock (_lock)
            {
                if (_paused && _heldLength + pcm.Length <= SampleRate * Block * MaxHeldSeconds)
                {
                    if (_heldLength + pcm.Length > _held.Length) Array.Resize(ref _held, Math.Max(_held.Length * 2, _heldLength + pcm.Length));
                    pcm.CopyTo(_held.AsSpan(_heldLength));
                    _heldLength += pcm.Length;
                    Interlocked.Add(ref _accepted, pcm.Length / Block);
                    return;
                }
            }
            _running.Wait(100, ct);
        }
        if (_disposed) return;
        SendHeld();
        Send(pcm);
        Interlocked.Add(ref _accepted, pcm.Length / Block);
    }

    private void SendHeld()
    {
        byte[] held;
        int len;
        lock (_lock)
        {
            if (_heldLength == 0) return;
            held = _held;
            len = _heldLength;
            _held = new byte[64 * 1024];
            _heldLength = 0;
        }
        Send(held.AsSpan(0, len));
    }

    private void Send(ReadOnlySpan<byte> pcm)
    {
        int offset = 0;
        while (offset < pcm.Length && !_disposed)
        {
            int n = Math.Min(_tmp.Length, pcm.Length - offset);
            AudioOutputs.CopyWithVolume(pcm.Slice(offset, n), _tmp.AsSpan(0, n), _volume);
            int e;
            fixed (byte* p = _tmp)
                if (pa_simple_write(_s, p, (nuint)n, &e) < 0) return;
            Interlocked.Add(ref _sent, n / Block);
            offset += n;
        }
    }

    public void Flush()
    {
        while (_paused && !_disposed) _running.Wait(100);
        if (!_disposed) SendHeld();
    }

    public void Pause()
    {
        if (_paused || _disposed) return;
        _paused = true;
        _running.Reset();
        lock (_lock)
        {
            int e;
            // Dane w buforze serwera przepadają (ok. 150 ms) — liczymy je jako odtworzone, więc zegar się zatrzymuje.
            ulong latencyUs = pa_simple_get_latency(_s, &e);
            pa_simple_flush(_s, &e);
            _ = latencyUs;
        }
    }

    public void Resume()
    {
        if (!_paused || _disposed) return;
        _paused = false;
        _running.Set();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _running.Set();
            int e;
            pa_simple_flush(_s, &e);
            pa_simple_free(_s);
            _s = IntPtr.Zero;
        }
    }
}
