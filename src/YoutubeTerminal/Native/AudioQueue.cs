using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YoutubeTerminal.Playback;

namespace YoutubeTerminal.Native;

/// <summary>
/// Dźwięk w macOS przez AudioQueue (AudioToolbox). Bufory wracają przez wywołanie zwrotne
/// na wątku kolejki; pozycja odtwarzania z AudioQueueGetCurrentTime (zatrzymuje się przy pauzie).
/// </summary>
internal sealed unsafe partial class AudioQueueOutput : IAudioOutput
{
    private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'
    private const uint kAudioFormatFlagIsSignedInteger = 0x4, kAudioFormatFlagIsPacked = 0x8;
    private const uint kAudioQueueParam_Volume = 1;
    private const uint kAudioTimeStampSampleTimeValid = 1;
    private const int BufferCount = 12;
    private const int FramesPerBuffer = 2048;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmpteTime
    {
        public short Subframes, SubframeDivisor;
        public uint Counter, Type, Flags;
        public short Hours, Minutes, Seconds, Frames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public SmpteTime Smpte;
        public uint Flags;
        public uint Reserved;
    }

    [LibraryImport(Lib)]
    private static partial int AudioQueueNewOutput(AudioStreamBasicDescription* format,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, AudioQueueBuffer*, void> callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, IntPtr* queue);

    [LibraryImport(Lib)] private static partial int AudioQueueAllocateBuffer(IntPtr queue, uint size, AudioQueueBuffer** buffer);
    [LibraryImport(Lib)] private static partial int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint packetDescs, IntPtr descs);
    [LibraryImport(Lib)] private static partial int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [LibraryImport(Lib)] private static partial int AudioQueuePause(IntPtr queue);
    [LibraryImport(Lib)] private static partial int AudioQueueStop(IntPtr queue, byte immediate);
    [LibraryImport(Lib)] private static partial int AudioQueueDispose(IntPtr queue, byte immediate);
    [LibraryImport(Lib)] private static partial int AudioQueueSetParameter(IntPtr queue, uint param, float value);
    [LibraryImport(Lib)] private static partial int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, AudioTimeStamp* time, byte* discontinuity);

    private IntPtr _queue;
    private GCHandle _self;
    private readonly AudioQueueBuffer*[] _buffers = new AudioQueueBuffer*[BufferCount];
    private readonly Queue<int> _free = new();
    private readonly AutoResetEvent _bufferReturned = new(false);
    private int _current = -1;
    private int _fill;
    private long _submitted;
    private bool _started;
    private volatile bool _disposed;
    private readonly int _bufferBytes;

    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>Kolejka startuje dopiero przy <see cref="Resume"/> — do tego czasu dane tylko się buforują.</summary>
    public bool Paused { get; private set; } = true;
    private int Block => Channels * 2;

    public AudioQueueOutput(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var fmt = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatID = kAudioFormatLinearPCM,
            FormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
            BytesPerPacket = (uint)(channels * 2),
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(channels * 2),
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 16,
        };
        _self = GCHandle.Alloc(this);
        IntPtr q;
        int st = AudioQueueNewOutput(&fmt, &OnBufferDone, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, &q);
        if (st != 0)
        {
            _self.Free();
            throw new InvalidOperationException(L.T($"Nie można otworzyć wyjścia dźwięku (AudioQueue {st}).", $"Cannot open the audio output (AudioQueue {st})."));
        }
        _queue = q;
        _bufferBytes = FramesPerBuffer * Block;
        for (int i = 0; i < BufferCount; i++)
        {
            AudioQueueBuffer* b;
            if (AudioQueueAllocateBuffer(_queue, (uint)_bufferBytes, &b) != 0) break;
            b->UserData = i;
            _buffers[i] = b;
            _free.Enqueue(i);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferDone(IntPtr user, IntPtr queue, AudioQueueBuffer* buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not AudioQueueOutput self) return;
            lock (self._free) self._free.Enqueue((int)buffer->UserData);
            self._bufferReturned.Set();
        }
        catch { }
    }

    public long SubmittedFrames => Interlocked.Read(ref _submitted);

    public long PlayedFrames
    {
        get
        {
            if (_disposed || !_started) return 0;
            AudioTimeStamp ts;
            byte disc;
            if (AudioQueueGetCurrentTime(_queue, IntPtr.Zero, &ts, &disc) != 0) return 0;
            if ((ts.Flags & kAudioTimeStampSampleTimeValid) == 0) return 0;
            return Math.Clamp((long)ts.SampleTime, 0, SubmittedFrames);
        }
    }

    public void SetVolume(float volume)
    {
        if (!_disposed) AudioQueueSetParameter(_queue, kAudioQueueParam_Volume, Math.Clamp(volume, 0f, 1f));
    }

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct)
    {
        int offset = 0;
        while (offset < pcm.Length && !_disposed)
        {
            if (_current < 0)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_free)
                        if (_free.Count > 0) { _current = _free.Dequeue(); _fill = 0; break; }
                    _bufferReturned.WaitOne(50);
                }
            }
            var b = _buffers[_current];
            int n = Math.Min(_bufferBytes - _fill, pcm.Length - offset);
            n -= n % 2;
            if (n <= 0) break;
            pcm.Slice(offset, n).CopyTo(new Span<byte>((byte*)b->AudioData + _fill, n));
            _fill += n;
            offset += n;
            if (_fill >= _bufferBytes) SubmitCurrent();
        }
    }

    private void SubmitCurrent()
    {
        var b = _buffers[_current];
        b->AudioDataByteSize = (uint)_fill;
        AudioQueueEnqueueBuffer(_queue, b, 0, IntPtr.Zero);
        Interlocked.Add(ref _submitted, _fill / Block);
        _current = -1;
        _fill = 0;
    }

    public void Flush()
    {
        if (_current >= 0 && _fill > 0) SubmitCurrent();
    }

    public void Pause()
    {
        if (_disposed || Paused) return;
        Paused = true;
        if (_started) AudioQueuePause(_queue);
    }

    public void Resume()
    {
        if (_disposed || !Paused) return;
        Paused = false;
        AudioQueueStart(_queue, IntPtr.Zero);
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AudioQueueStop(_queue, 1);
        AudioQueueDispose(_queue, 1); // zwalnia też bufory
        _queue = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
        _bufferReturned.Dispose();
    }
}
