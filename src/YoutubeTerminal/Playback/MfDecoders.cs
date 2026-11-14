using System.Buffers.Binary;
using System.Runtime.InteropServices;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Playback;

/// <summary>Wspólna obsługa dekodera MFT (ProcessInput/ProcessOutput, renegocjacja formatu).</summary>
internal abstract unsafe class MftDecoder : IDisposable
{
    protected IntPtr Mft;
    private IntPtr _outSample;
    private IntPtr _outBuffer;
    private uint _outBufferSize;
    private bool _providesSamples;

    protected void Start()
    {
        MF.T_ProcessMessage(Mft, MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING);
        MF.T_ProcessMessage(Mft, MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM);
    }

    protected abstract bool IsWantedOutput(IntPtr mediaType);
    protected abstract void OnOutputTypeSet(IntPtr mediaType);
    protected abstract void OnOutput(byte* data, int length, long time);

    protected void NegotiateOutput()
    {
        for (uint i = 0; ; i++)
        {
            int hr = MF.T_GetOutputAvailableType(Mft, i, out IntPtr mt);
            if (hr == MF.MF_E_NO_MORE_TYPES || hr < 0)
                throw new MediaFoundationException("Dekoder nie oferuje obsługiwanego formatu wyjściowego", hr);
            try
            {
                if (IsWantedOutput(mt))
                {
                    MF.Check(MF.T_SetOutputType(Mft, mt), "SetOutputType");
                    break;
                }
            }
            finally
            {
                MF.Release(ref mt);
            }
        }

        MF.Check(MF.T_GetOutputCurrentType(Mft, out IntPtr cur), "GetOutputCurrentType");
        try { OnOutputTypeSet(cur); }
        finally { MF.Release(ref cur); }

        MF.Check(MF.T_GetOutputStreamInfo(Mft, out var info), "GetOutputStreamInfo");
        _providesSamples = (info.dwFlags & (MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MF.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        if (!_providesSamples && info.cbSize > _outBufferSize)
        {
            MF.Release(ref _outBuffer);
            MF.Release(ref _outSample);
            _outBufferSize = Math.Max(info.cbSize, 4096);
            MF.Check(MF.MFCreateSample(out _outSample), "MFCreateSample");
            MF.Check(MF.MFCreateMemoryBuffer(_outBufferSize, out _outBuffer), "MFCreateMemoryBuffer");
            MF.Check(MF.Sample_AddBuffer(_outSample, _outBuffer), "AddBuffer");
        }
    }

    protected void Input(ReadOnlySpan<byte> data, long time, long duration, bool key)
    {
        IntPtr sample = MF.CreateSampleFromData(data, time, duration);
        try
        {
            if (key) MF.SetUINT32(sample, MF.MFSampleExtension_CleanPoint, 1);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                int hr = MF.T_ProcessInput(Mft, sample);
                if (hr == MF.MF_E_NOTACCEPTING)
                {
                    DrainOutput();
                    continue;
                }
                MF.Check(hr, "ProcessInput");
                break;
            }
        }
        finally
        {
            MF.Release(ref sample);
        }
        DrainOutput();
    }

    public void EndOfStream()
    {
        MF.T_ProcessMessage(Mft, MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM);
        MF.T_ProcessMessage(Mft, MF.MFT_MESSAGE_COMMAND_DRAIN);
        DrainOutput();
    }

    private void DrainOutput()
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            var buf = new MF.MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = _providesSamples ? IntPtr.Zero : _outSample };
            if (!_providesSamples) MF.Buffer_SetCurrentLength(_outBuffer, 0);
            int hr = MF.T_ProcessOutput(Mft, ref buf, out _);
            if (buf.pEvents != IntPtr.Zero) MF.Release(ref buf.pEvents);

            if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (_providesSamples && buf.pSample != IntPtr.Zero) MF.Release(ref buf.pSample);
                NegotiateOutput();
                continue;
            }
            MF.Check(hr, "ProcessOutput");

            IntPtr sample = buf.pSample;
            if (sample == IntPtr.Zero) continue;
            try
            {
                long time = MF.Sample_GetTime(sample);
                MF.Check(MF.Sample_ConvertToContiguousBuffer(sample, out IntPtr buffer), "ConvertToContiguousBuffer");
                try
                {
                    MF.Check(MF.Buffer_Lock(buffer, out byte* data, out _, out uint len), "Lock");
                    try { OnOutput(data, (int)len, time); }
                    finally { MF.Buffer_Unlock(buffer); }
                }
                finally
                {
                    MF.Release(ref buffer);
                }
            }
            finally
            {
                if (_providesSamples) MF.Release(ref sample);
            }
        }
    }

    public virtual void Dispose()
    {
        if (Mft != IntPtr.Zero)
        {
            MF.T_ProcessMessage(Mft, MF.MFT_MESSAGE_COMMAND_FLUSH);
        }
        MF.Release(ref _outBuffer);
        MF.Release(ref _outSample);
        MF.Release(ref Mft);
    }
}

/// <summary>Dekoder H.264 (systemowy CMSH264DecoderMFT) — wyjście NV12.</summary>
internal sealed unsafe class H264Decoder : MftDecoder
{
    private readonly byte[] _paramSets;
    private readonly int _nalLengthSize;
    private byte[] _annexB = new byte[256 * 1024];

    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public int Stride { get; private set; }
    public int CropX { get; private set; }
    public int CropY { get; private set; }
    public int DisplayWidth { get; private set; }
    public int DisplayHeight { get; private set; }

    /// <summary>Wywoływane dla każdej zdekodowanej klatki: (czas w 100 ns, Y, UV, stride).</summary>
    public delegate void FrameHandler(long time, byte* y, byte* uv, int stride);
    public FrameHandler? OnFrame;

    private readonly bool _inputIsAnnexB;

    /// <param name="avcConfig">avcC z MP4 albo null, gdy wejście to już Annex B z SPS/PPS w strumieniu (MPEG-TS).</param>
    public H264Decoder(byte[]? avcConfig, int width, int height)
    {
        if (avcConfig != null) (_paramSets, _nalLengthSize) = ParseAvcC(avcConfig);
        else { _paramSets = []; _nalLengthSize = 4; _inputIsAnnexB = true; }
        Mft = MF.CreateTransform(MF.CLSID_CMSH264DecoderMFT);

        MF.Check(MF.MFCreateMediaType(out IntPtr mt), "MFCreateMediaType");
        try
        {
            MF.SetGUID(mt, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            MF.SetGUID(mt, MF.MF_MT_SUBTYPE, MF.MFVideoFormat_H264);
            if (width > 0 && height > 0)
                MF.SetUINT64(mt, MF.MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
            MF.SetUINT32(mt, MF.MF_MT_INTERLACE_MODE, 2); // progresywny
            MF.Check(MF.T_SetInputType(Mft, mt), "SetInputType (H.264)");
        }
        finally
        {
            MF.Release(ref mt);
        }
        NegotiateOutput();
        Start();
    }

    private static (byte[] ParamSets, int NalLen) ParseAvcC(byte[] c)
    {
        if (c.Length < 7) throw new InvalidDataException("Nieprawidłowa konfiguracja AVC.");
        int nalLen = (c[4] & 3) + 1;
        var ms = new MemoryStream();
        int p = 5;
        int numSps = c[p++] & 0x1F;
        for (int i = 0; i < numSps && p + 2 <= c.Length; i++)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(p)); p += 2;
            ms.Write([0, 0, 0, 1]);
            ms.Write(c, p, Math.Min(len, c.Length - p));
            p += len;
        }
        if (p < c.Length)
        {
            int numPps = c[p++];
            for (int i = 0; i < numPps && p + 2 <= c.Length; i++)
            {
                int len = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(p)); p += 2;
                ms.Write([0, 0, 0, 1]);
                ms.Write(c, p, Math.Min(len, c.Length - p));
                p += len;
            }
        }
        return (ms.ToArray(), nalLen);
    }

    protected override bool IsWantedOutput(IntPtr mt)
        => MF.GetGUID(mt, MF.MF_MT_SUBTYPE, out var sub) >= 0 && sub == MF.MFVideoFormat_NV12;

    protected override void OnOutputTypeSet(IntPtr mt)
    {
        MF.GetUINT64(mt, MF.MF_MT_FRAME_SIZE, out ulong size);
        FrameWidth = (int)(size >> 32);
        FrameHeight = (int)(size & 0xFFFFFFFF);
        Stride = MF.GetUINT32(mt, MF.MF_MT_DEFAULT_STRIDE, out uint stride) >= 0 && (int)stride > 0 ? (int)stride : FrameWidth;
        CropX = 0; CropY = 0; DisplayWidth = FrameWidth; DisplayHeight = FrameHeight;
        byte* area = stackalloc byte[16];
        if (MF.GetBlob(mt, MF.MF_MT_MINIMUM_DISPLAY_APERTURE, area, 16, out uint written) >= 0 && written >= 16)
        {
            int ox = *(short*)(area + 2), oy = *(short*)(area + 6);
            int cx = *(int*)(area + 8), cy = *(int*)(area + 12);
            if (cx > 0 && cy > 0 && ox >= 0 && oy >= 0 && ox + cx <= FrameWidth && oy + cy <= FrameHeight)
            {
                CropX = ox; CropY = oy; DisplayWidth = cx; DisplayHeight = cy;
            }
        }
    }

    protected override void OnOutput(byte* data, int length, long time)
    {
        if (OnFrame == null || FrameWidth == 0) return;
        long ySize = (long)Stride * FrameHeight;
        if (length < ySize + ySize / 2) return;
        OnFrame(time, data, data + ySize, Stride);
    }

    /// <summary>Przekazuje próbkę do dekodera (AVCC jest konwertowane na Annex B).</summary>
    public void Decode(in MediaSample s)
    {
        long time100ns = s.Time, duration100ns = s.Duration;
        if (_inputIsAnnexB)
        {
            Input(s.Data, time100ns, duration100ns, s.IsKey);
            return;
        }
        var src = s.Data;
        int needed = src.Length + (s.IsKey ? _paramSets.Length : 0) + 64 * 4;
        if (_annexB.Length < needed * 2) _annexB = new byte[needed * 2];
        int o = 0;
        if (s.IsKey)
        {
            _paramSets.CopyTo(_annexB, 0);
            o = _paramSets.Length;
        }
        int p = 0;
        while (p + _nalLengthSize <= src.Length)
        {
            int len = 0;
            for (int i = 0; i < _nalLengthSize; i++) len = (len << 8) | src[p + i];
            p += _nalLengthSize;
            if (len <= 0 || p + len > src.Length) break;
            if (o + len + 4 > _annexB.Length) Array.Resize(ref _annexB, (o + len + 4) * 2);
            _annexB[o] = 0; _annexB[o + 1] = 0; _annexB[o + 2] = 0; _annexB[o + 3] = 1;
            src.Slice(p, len).CopyTo(_annexB.AsSpan(o + 4));
            o += len + 4;
            p += len;
        }
        if (o == 0) return;
        Input(_annexB.AsSpan(0, o), time100ns, duration100ns, s.IsKey);
    }
}

/// <summary>Dekoder AAC (systemowy CMSAACDecMFT) — wyjście PCM 16-bit.</summary>
internal sealed unsafe class AacDecoder : MftDecoder
{
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    public delegate void PcmHandler(ReadOnlySpan<byte> pcm, long time);
    public PcmHandler? OnPcm;

    public AacDecoder(byte[]? audioSpecificConfig, int sampleRate, int channels)
    {
        Mft = MF.CreateTransform(MF.CLSID_CMSAACDecMFT);
        var asc = audioSpecificConfig ?? BuildAsc(sampleRate, channels);
        (int ascRate, int ascChannels) = ParseAsc(asc);
        if (sampleRate <= 0) sampleRate = ascRate;
        if (channels <= 0) channels = ascChannels;

        MF.Check(MF.MFCreateMediaType(out IntPtr mt), "MFCreateMediaType");
        try
        {
            MF.SetGUID(mt, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
            MF.SetGUID(mt, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_AAC);
            MF.SetUINT32(mt, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)(ascRate > 0 ? ascRate : sampleRate));
            MF.SetUINT32(mt, MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)(ascChannels > 0 ? ascChannels : channels));
            MF.SetUINT32(mt, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            MF.SetUINT32(mt, MF.MF_MT_AAC_PAYLOAD_TYPE, 0);
            MF.SetUINT32(mt, MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
            var user = new byte[12 + asc.Length];
            user[2] = 0x29; // wAudioProfileLevelIndication
            asc.CopyTo(user, 12);
            MF.SetBlob(mt, MF.MF_MT_USER_DATA, user);
            MF.Check(MF.T_SetInputType(Mft, mt), "SetInputType (AAC)");
        }
        finally
        {
            MF.Release(ref mt);
        }
        NegotiateOutput();
        Start();
    }

    private static (int rate, int channels) ParseAsc(byte[] asc)
    {
        if (asc.Length < 2) return (0, 0);
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int bits = (asc[0] << 8) | asc[1];
        int objectType = bits >> 11;
        int freqIdx = (bits >> 7) & 0xF;
        int ch = (bits >> 3) & 0xF;
        _ = objectType;
        int rate = freqIdx < rates.Length ? rates[freqIdx] : 0;
        return (rate, ch);
    }

    private static byte[] BuildAsc(int sampleRate, int channels)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int idx = Array.IndexOf(rates, sampleRate);
        if (idx < 0) idx = 4;
        int v = (2 << 11) | (idx << 7) | ((channels & 0xF) << 3);
        return [(byte)(v >> 8), (byte)v];
    }

    protected override bool IsWantedOutput(IntPtr mt)
        => MF.GetGUID(mt, MF.MF_MT_SUBTYPE, out var sub) >= 0 && sub == MF.MFAudioFormat_PCM
           && MF.GetUINT32(mt, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bits) >= 0 && bits == 16;

    protected override void OnOutputTypeSet(IntPtr mt)
    {
        MF.GetUINT32(mt, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint rate);
        MF.GetUINT32(mt, MF.MF_MT_AUDIO_NUM_CHANNELS, out uint ch);
        SampleRate = (int)rate;
        Channels = (int)ch;
    }

    protected override void OnOutput(byte* data, int length, long time)
        => OnPcm?.Invoke(new ReadOnlySpan<byte>(data, length), time);

    public void Decode(in MediaSample s) => Input(s.Data, s.Time, s.Duration, true);
}
