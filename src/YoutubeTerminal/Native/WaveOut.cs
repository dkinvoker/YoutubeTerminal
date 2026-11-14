using System.Runtime.InteropServices;

namespace YoutubeTerminal.Native;

/// <summary>
/// Wyjście audio przez WinMM waveOut (dostępne w każdym Windows). Bufory w pamięci niezarządzanej,
/// pozycja odtwarzania (zegar A/V) z waveOutGetPosition.
/// </summary>
internal sealed unsafe partial class WaveOut : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMTIME
    {
        public uint wType;
        public uint value;
        public uint pad;
    }

    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 0x1;
    private const uint TIME_SAMPLES = 0x2;

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutOpen(out IntPtr hwo, uint device, WAVEFORMATEX* format, IntPtr callback, IntPtr instance, uint flags);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPrepareHeader(IntPtr hwo, WAVEHDR* hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutUnprepareHeader(IntPtr hwo, WAVEHDR* hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutWrite(IntPtr hwo, WAVEHDR* hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPause(IntPtr hwo);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutRestart(IntPtr hwo);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutReset(IntPtr hwo);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutClose(IntPtr hwo);
    [LibraryImport("winmm.dll")]
    private static partial uint waveOutGetPosition(IntPtr hwo, MMTIME* time, uint size);

    private IntPtr _hwo;
    private readonly WAVEHDR*[] _headers;
    private readonly int _bufferBytes;
    private readonly AutoResetEvent _event = new(false);
    private readonly Queue<int> _free = new();
    private int _fill;      // ile bajtów zapisano w bieżącym buforze
    private int _current = -1;
    private long _submittedFrames;
    private uint _lastPos;
    private long _posWraps;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BlockAlign => Channels * 2;
    public bool Paused { get; private set; }

    public WaveOut(int sampleRate, int channels, int bufferCount = 12, int framesPerBuffer = 2048)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(channels * 2),
            nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
        };
        uint r = waveOutOpen(out _hwo, WAVE_MAPPER, &fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (r != 0) throw new InvalidOperationException($"Nie można otworzyć urządzenia audio (kod {r}).");

        _bufferBytes = framesPerBuffer * BlockAlign;
        _headers = new WAVEHDR*[bufferCount];
        for (int i = 0; i < bufferCount; i++)
        {
            var h = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)sizeof(WAVEHDR));
            h->lpData = (IntPtr)NativeMemory.AllocZeroed((nuint)_bufferBytes);
            h->dwBufferLength = (uint)_bufferBytes;
            waveOutPrepareHeader(_hwo, h, (uint)sizeof(WAVEHDR));
            _headers[i] = h;
            _free.Enqueue(i);
        }
    }

    /// <summary>Liczba ramek (próbek na kanał) przekazanych do urządzenia.</summary>
    public long SubmittedFrames => _submittedFrames;

    /// <summary>Liczba odtworzonych ramek od otwarcia urządzenia.</summary>
    public long PlayedFrames
    {
        get
        {
            if (_disposed) return 0;
            MMTIME t = new() { wType = TIME_SAMPLES };
            if (waveOutGetPosition(_hwo, &t, (uint)sizeof(MMTIME)) != 0) return 0;
            uint pos = t.value;
            if (t.wType != TIME_SAMPLES) pos = (uint)(pos / BlockAlign);
            lock (_free)
            {
                if (pos < _lastPos && _lastPos - pos > 0x80000000u) _posWraps++;
                _lastPos = pos;
                return (_posWraps << 32) + pos;
            }
        }
    }

    public int QueuedBuffers
    {
        get
        {
            Reclaim();
            lock (_free) return _headers.Length - _free.Count - (_current >= 0 ? 1 : 0);
        }
    }

    public TimeSpan BufferedDuration => TimeSpan.FromSeconds(Math.Max(0, SubmittedFrames - PlayedFrames) / (double)SampleRate);

    private void Reclaim()
    {
        lock (_free)
        {
            for (int i = 0; i < _headers.Length; i++)
            {
                var h = _headers[i];
                if ((h->dwFlags & WHDR_DONE) != 0 && h->dwUser == (IntPtr)1)
                {
                    h->dwUser = IntPtr.Zero;
                    _free.Enqueue(i);
                }
            }
        }
    }

    /// <summary>
    /// Zapisuje PCM. Blokuje, gdy wszystkie bufory są zajęte (aż urządzenie zwolni któryś lub upłynie limit).
    /// Głośność (0..1) stosowana programowo.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> pcm, float volume, CancellationToken ct)
    {
        int offset = 0;
        while (offset < pcm.Length)
        {
            if (_current < 0)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Reclaim();
                    lock (_free)
                    {
                        if (_free.Count > 0) { _current = _free.Dequeue(); _fill = 0; break; }
                    }
                    _event.WaitOne(50);
                }
            }

            var h = _headers[_current];
            int n = Math.Min(_bufferBytes - _fill, pcm.Length - offset);
            n -= n % 2;
            if (n <= 0) break;
            CopyWithVolume(pcm.Slice(offset, n), new Span<byte>((byte*)h->lpData + _fill, n), volume);
            _fill += n;
            offset += n;
            if (_fill >= _bufferBytes) SubmitCurrent();
        }
        return true;
    }

    /// <summary>Wysyła częściowo wypełniony bufor (np. na końcu strumienia).</summary>
    public void Flush()
    {
        if (_current >= 0 && _fill > 0) SubmitCurrent();
    }

    private void SubmitCurrent()
    {
        var h = _headers[_current];
        h->dwBufferLength = (uint)_fill;
        h->dwFlags &= ~WHDR_DONE;
        h->dwUser = (IntPtr)1;
        waveOutWrite(_hwo, h, (uint)sizeof(WAVEHDR));
        _submittedFrames += _fill / BlockAlign;
        _current = -1;
        _fill = 0;
    }

    private static void CopyWithVolume(ReadOnlySpan<byte> src, Span<byte> dst, float volume)
    {
        if (volume >= 0.999f)
        {
            src.CopyTo(dst);
            return;
        }
        var s = MemoryMarshal.Cast<byte, short>(src);
        var d = MemoryMarshal.Cast<byte, short>(dst);
        int v = (int)(volume * 65536);
        for (int i = 0; i < s.Length; i++)
            d[i] = (short)((s[i] * v) >> 16);
    }

    public void Pause()
    {
        if (_disposed || Paused) return;
        waveOutPause(_hwo);
        Paused = true;
    }

    public void Resume()
    {
        if (_disposed || !Paused) return;
        waveOutRestart(_hwo);
        Paused = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        waveOutReset(_hwo);
        foreach (var h in _headers)
        {
            waveOutUnprepareHeader(_hwo, h, (uint)sizeof(WAVEHDR));
            NativeMemory.Free((void*)h->lpData);
            NativeMemory.Free(h);
        }
        waveOutClose(_hwo);
        _event.Dispose();
    }
}
