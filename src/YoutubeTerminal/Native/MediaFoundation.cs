using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YoutubeTerminal.Native;

/// <summary>
/// Minimalny, zgodny z Native AOT dostęp do Windows Media Foundation (Source Reader).
/// Interfejsy COM są wywoływane bezpośrednio przez tablice wirtualne (function pointers),
/// dzięki czemu nie potrzebujemy refleksji ani generowanych wrapperów.
/// </summary>
internal static unsafe partial class MF
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MFSTARTUP_FULL = 0;

    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

    public const uint MF_SOURCE_READERF_ERROR = 0x1;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    public const uint MF_SOURCE_READERF_NEWSTREAM = 0x4;
    public const uint MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED = 0x10;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x100;

    public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0F81DA2C-B537-4672-A8B2-A681B17307A3");
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("A634A91C-822B-41B9-A494-4DE4643612B0");
    public static readonly Guid MF_LOW_LATENCY = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");

    public static readonly Guid MF_MT_USER_DATA = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid MFSampleExtension_Discontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

    public static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4d20-B15D-452831A87D9D");
    public static readonly Guid CLSID_CMSAACDecMFT = new("32D186A7-218F-4C75-8876-DD77273A8999");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const uint MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, IntPtr outer, uint clsContext, Guid* iid, IntPtr* ppv);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(out IntPtr sample);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    public static IntPtr CreateTransform(in Guid clsid)
    {
        EnsureStarted();
        IntPtr p;
        Guid c = clsid, iid = IID_IMFTransform;
        Check(CoCreateInstance(&c, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, &iid, &p), "Utworzenie dekodera");
        return p;
    }

    public static int SetBlob(IntPtr attr, in Guid key, ReadOnlySpan<byte> data)
    {
        fixed (Guid* k = &key)
        fixed (byte* d = data)
            return ((delegate* unmanaged<IntPtr, Guid*, byte*, uint, int>)Slot(attr, 26))(attr, k, d, (uint)data.Length);
    }

    // IMFSample — zapis
    public static int Sample_SetTime(IntPtr s, long t) => ((delegate* unmanaged<IntPtr, long, int>)Slot(s, 36))(s, t);
    public static int Sample_SetDuration(IntPtr s, long t) => ((delegate* unmanaged<IntPtr, long, int>)Slot(s, 38))(s, t);
    public static int Sample_AddBuffer(IntPtr s, IntPtr b) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(s, 42))(s, b);
    public static int Sample_RemoveAllBuffers(IntPtr s) => ((delegate* unmanaged<IntPtr, int>)Slot(s, 44))(s);

    public static int Buffer_SetCurrentLength(IntPtr b, uint len) => ((delegate* unmanaged<IntPtr, uint, int>)Slot(b, 6))(b, len);

    public static uint Buffer_GetMaxLength(IntPtr b)
    {
        uint m = 0;
        ((delegate* unmanaged<IntPtr, uint*, int>)Slot(b, 7))(b, &m);
        return m;
    }

    /// <summary>Tworzy próbkę MF z kopią danych.</summary>
    public static IntPtr CreateSampleFromData(ReadOnlySpan<byte> data, long time, long duration)
    {
        Check(MFCreateSample(out IntPtr sample), "MFCreateSample");
        IntPtr buffer = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)Math.Max(1, data.Length), out buffer), "MFCreateMemoryBuffer");
            Check(Buffer_Lock(buffer, out byte* p, out _, out _), "Lock");
            data.CopyTo(new Span<byte>(p, data.Length));
            Buffer_Unlock(buffer);
            Buffer_SetCurrentLength(buffer, (uint)data.Length);
            Check(Sample_AddBuffer(sample, buffer), "AddBuffer");
            Sample_SetTime(sample, time);
            if (duration > 0) Sample_SetDuration(sample, duration);
            return sample;
        }
        catch
        {
            Release(ref sample);
            throw;
        }
        finally
        {
            Release(ref buffer);
        }
    }

    // IMFTransform (IUnknown 0..2)
    public struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        public IntPtr pSample;
        public uint dwStatus;
        public IntPtr pEvents;
    }

    public static int T_GetOutputStreamInfo(IntPtr t, out MFT_OUTPUT_STREAM_INFO info)
    {
        MFT_OUTPUT_STREAM_INFO i;
        int hr = ((delegate* unmanaged<IntPtr, uint, MFT_OUTPUT_STREAM_INFO*, int>)Slot(t, 7))(t, 0, &i);
        info = i;
        return hr;
    }

    public static IntPtr T_GetAttributes(IntPtr t)
    {
        IntPtr a = IntPtr.Zero;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(t, 8))(t, &a);
        return hr >= 0 ? a : IntPtr.Zero;
    }

    public static int T_GetOutputAvailableType(IntPtr t, uint index, out IntPtr type)
    {
        IntPtr mt = IntPtr.Zero;
        int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Slot(t, 14))(t, 0, index, &mt);
        type = mt;
        return hr;
    }

    public static int T_SetInputType(IntPtr t, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Slot(t, 15))(t, 0, type, 0);
    public static int T_SetOutputType(IntPtr t, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Slot(t, 16))(t, 0, type, 0);

    public static int T_GetOutputCurrentType(IntPtr t, out IntPtr type)
    {
        IntPtr mt = IntPtr.Zero;
        int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(t, 18))(t, 0, &mt);
        type = mt;
        return hr;
    }

    public static int T_ProcessMessage(IntPtr t, uint msg, nuint param = 0) => ((delegate* unmanaged<IntPtr, uint, nuint, int>)Slot(t, 23))(t, msg, param);
    public static int T_ProcessInput(IntPtr t, IntPtr sample) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Slot(t, 24))(t, 0, sample, 0);

    public static int T_ProcessOutput(IntPtr t, ref MFT_OUTPUT_DATA_BUFFER buffer, out uint status)
    {
        uint st = 0;
        int hr;
        fixed (MFT_OUTPUT_DATA_BUFFER* b = &buffer)
            hr = ((delegate* unmanaged<IntPtr, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)Slot(t, 25))(t, 0, 1, b, &st);
        status = st;
        return hr;
    }

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out IntPtr mediaType);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr sourceReader);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(void* pvar);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    private static bool _started;
    private static readonly object StartLock = new();
    [ThreadStatic] private static bool _threadInit;

    /// <summary>Inicjalizuje COM (MTA) w bieżącym wątku oraz Media Foundation (raz na proces).</summary>
    public static void EnsureStarted()
    {
        if (!_threadInit)
        {
            CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
            _threadInit = true;
        }
        lock (StartLock)
        {
            if (_started) return;
            Check(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
            _started = true;
        }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaFoundationException(what, hr);
    }

    // ---- Wywołania przez vtable ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Slot(IntPtr p, int index) => (*(void***)p)[index];

    public static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, uint>)Slot(p, 2))(p);
            p = IntPtr.Zero;
        }
    }

    // IMFAttributes (sloty 3..32)
    public static int GetUINT32(IntPtr attr, in Guid key, out uint value)
    {
        uint v = 0;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Slot(attr, 7))(attr, k, &v);
        value = v;
        return hr;
    }

    public static int GetUINT64(IntPtr attr, in Guid key, out ulong value)
    {
        ulong v = 0;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Slot(attr, 8))(attr, k, &v);
        value = v;
        return hr;
    }

    public static int GetGUID(IntPtr attr, in Guid key, out Guid value)
    {
        Guid v;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(attr, 10))(attr, k, &v);
        value = v;
        return hr;
    }

    public static int GetBlob(IntPtr attr, in Guid key, void* buffer, uint size, out uint written)
    {
        uint w = 0;
        int hr;
        fixed (Guid* k = &key) hr = ((delegate* unmanaged<IntPtr, Guid*, void*, uint, uint*, int>)Slot(attr, 15))(attr, k, buffer, size, &w);
        written = w;
        return hr;
    }

    public static int SetUINT32(IntPtr attr, in Guid key, uint value)
    {
        fixed (Guid* k = &key) return ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Slot(attr, 21))(attr, k, value);
    }

    public static int SetUINT64(IntPtr attr, in Guid key, ulong value)
    {
        fixed (Guid* k = &key) return ((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Slot(attr, 22))(attr, k, value);
    }

    public static int SetGUID(IntPtr attr, in Guid key, in Guid value)
    {
        fixed (Guid* k = &key)
        fixed (Guid* v = &value)
            return ((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(attr, 24))(attr, k, v);
    }

    // IMFSourceReader
    public static int SR_SetStreamSelection(IntPtr r, uint stream, bool selected)
        => ((delegate* unmanaged<IntPtr, uint, int, int>)Slot(r, 4))(r, stream, selected ? 1 : 0);

    public static int SR_GetNativeMediaType(IntPtr r, uint stream, uint index, out IntPtr mediaType)
    {
        IntPtr mt;
        int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Slot(r, 5))(r, stream, index, &mt);
        mediaType = mt;
        return hr;
    }

    public static int SR_GetCurrentMediaType(IntPtr r, uint stream, out IntPtr mediaType)
    {
        IntPtr mt;
        int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(r, 6))(r, stream, &mt);
        mediaType = mt;
        return hr;
    }

    public static int SR_SetCurrentMediaType(IntPtr r, uint stream, IntPtr mediaType)
        => ((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Slot(r, 7))(r, stream, null, mediaType);

    public static int SR_SetCurrentPosition(IntPtr r, long position100ns)
    {
        // PROPVARIANT: vt (VT_I8 = 20) + 6 bajtów rezerwy + wartość. 24 bajty na x64.
        byte* pv = stackalloc byte[24];
        new Span<byte>(pv, 24).Clear();
        *(ushort*)pv = 20;
        *(long*)(pv + 8) = position100ns;
        Guid empty = Guid.Empty;
        return ((delegate* unmanaged<IntPtr, Guid*, void*, int>)Slot(r, 8))(r, &empty, pv);
    }

    public static int SR_ReadSample(IntPtr r, uint stream, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample)
    {
        uint a = 0, f = 0;
        long ts = 0;
        IntPtr s = IntPtr.Zero;
        int hr = ((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(r, 9))(r, stream, 0, &a, &f, &ts, &s);
        actualStream = a;
        flags = f;
        timestamp = ts;
        sample = s;
        return hr;
    }

    public static long SR_GetDuration(IntPtr r)
    {
        byte* pv = stackalloc byte[24];
        new Span<byte>(pv, 24).Clear();
        Guid key = MF_PD_DURATION;
        int hr = ((delegate* unmanaged<IntPtr, uint, Guid*, void*, int>)Slot(r, 12))(r, MF_SOURCE_READER_MEDIASOURCE, &key, pv);
        if (hr < 0) return 0;
        long value = *(long*)(pv + 8);
        PropVariantClear(pv);
        return value;
    }

    // IMFSample (IMFAttributes 3..32, potem metody próbki)
    public static long Sample_GetTime(IntPtr s)
    {
        long t = 0;
        ((delegate* unmanaged<IntPtr, long*, int>)Slot(s, 35))(s, &t);
        return t;
    }

    public static long Sample_GetDuration(IntPtr s)
    {
        long t = 0;
        ((delegate* unmanaged<IntPtr, long*, int>)Slot(s, 37))(s, &t);
        return t;
    }

    public static int Sample_ConvertToContiguousBuffer(IntPtr s, out IntPtr buffer)
    {
        IntPtr b;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(s, 41))(s, &b);
        buffer = b;
        return hr;
    }

    // IMFMediaBuffer
    public static int Buffer_Lock(IntPtr b, out byte* data, out uint maxLength, out uint currentLength)
    {
        byte* d;
        uint m, c;
        int hr = ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Slot(b, 3))(b, &d, &m, &c);
        data = d;
        maxLength = m;
        currentLength = c;
        return hr;
    }

    public static int Buffer_Unlock(IntPtr b) => ((delegate* unmanaged<IntPtr, int>)Slot(b, 4))(b);
}

internal sealed class MediaFoundationException : Exception
{
    public int HResult32 { get; }

    public MediaFoundationException(string what, int hr)
        : base($"{what} nie powiodło się (HRESULT 0x{hr:X8}){Describe(hr)}")
    {
        HResult32 = hr;
    }

    private static string Describe(int hr) => (uint)hr switch
    {
        0xC00D36C4 => " – nieobsługiwany format/kodek",
        0xC00D36E6 => " – nieprawidłowy URL",
        0xC00D36B4 => " – nieprawidłowy typ mediów",
        0x80072EE7 => " – nie można rozwiązać nazwy serwera",
        0xC00D0FD4 or 0x80190193 => " – dostęp zabroniony (403)",
        _ => ""
    };
}
