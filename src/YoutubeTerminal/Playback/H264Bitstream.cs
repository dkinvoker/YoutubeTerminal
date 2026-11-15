using System.Buffers.Binary;

namespace YoutubeTerminal.Playback;

/// <summary>
/// Konwersja próbek H.264 z MP4 (AVCC: NAL z prefiksem długości) do strumienia Annex B (kody startu),
/// z SPS/PPS z avcC przed każdą klatką kluczową. Próbki z MPEG-TS są już w Annex B.
/// </summary>
internal sealed class H264Bitstream
{
    private readonly byte[] _paramSets;
    private readonly int _nalLengthSize;
    private readonly bool _inputIsAnnexB;
    private byte[] _buf = new byte[256 * 1024];

    /// <param name="avcConfig">avcC z MP4 albo null, gdy wejście to już Annex B.</param>
    public H264Bitstream(byte[]? avcConfig)
    {
        if (avcConfig != null) (_paramSets, _nalLengthSize) = ParseAvcC(avcConfig);
        else { _paramSets = []; _nalLengthSize = 4; _inputIsAnnexB = true; }
    }

    /// <summary>SPS i PPS z avcC jako Annex B.</summary>
    public ReadOnlySpan<byte> ParameterSets => _paramSets;

    public static (byte[] ParamSets, int NalLength) ParseAvcC(byte[] c)
    {
        if (c.Length < 7) throw new InvalidDataException(L.T("Nieprawidłowa konfiguracja AVC.", "Invalid AVC configuration."));
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

    /// <summary>Próbka jako Annex B. Wynik jest ważny do następnego wywołania.</summary>
    public ReadOnlySpan<byte> ToAnnexB(in MediaSample s)
    {
        if (_inputIsAnnexB) return s.Data;
        var src = s.Data;
        int needed = src.Length + (s.IsKey ? _paramSets.Length : 0) + 64 * 4;
        if (_buf.Length < needed * 2) _buf = new byte[needed * 2];
        int o = 0;
        if (s.IsKey)
        {
            _paramSets.CopyTo(_buf, 0);
            o = _paramSets.Length;
        }
        int p = 0;
        while (p + _nalLengthSize <= src.Length)
        {
            int len = 0;
            for (int i = 0; i < _nalLengthSize; i++) len = (len << 8) | src[p + i];
            p += _nalLengthSize;
            if (len <= 0 || p + len > src.Length) break;
            if (o + len + 4 > _buf.Length) Array.Resize(ref _buf, (o + len + 4) * 2);
            _buf[o] = 0; _buf[o + 1] = 0; _buf[o + 2] = 0; _buf[o + 3] = 1;
            src.Slice(p, len).CopyTo(_buf.AsSpan(o + 4));
            o += len + 4;
            p += len;
        }
        return _buf.AsSpan(0, o);
    }
}

/// <summary>Nagłówki ADTS dla surowych ramek AAC (dla dekoderów czytających strumień .aac).</summary>
internal static class Adts
{
    /// <summary>Profil, indeks częstotliwości i konfiguracja kanałów z AudioSpecificConfig (lub z parametrów).</summary>
    public static (int Profile, int FreqIndex, int Channels) FromConfig(byte[]? asc, int sampleRate, int channels)
    {
        if (asc is { Length: >= 2 })
        {
            int objectType = asc[0] >> 3;
            int freq = ((asc[0] & 7) << 1) | (asc[1] >> 7);
            int ch = (asc[1] >> 3) & 0xF;
            // ADTS zna tylko profile 1–4; HE-AAC (SBR/PS) zapisujemy jako LC — dekoder sam wykryje SBR.
            int profile = objectType is >= 1 and <= 4 ? objectType : 2;
            return (profile, freq, ch > 0 ? ch : Math.Max(1, channels));
        }
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int idx = Array.IndexOf(rates, sampleRate);
        return (2, idx < 0 ? 4 : idx, Math.Max(1, channels));
    }

    public static void WriteHeader(Span<byte> h, int profile, int freqIndex, int channels, int payloadLength)
    {
        int len = payloadLength + 7;
        h[0] = 0xFF;
        h[1] = 0xF1; // MPEG-4, bez CRC
        h[2] = (byte)((((profile - 1) & 3) << 6) | ((freqIndex & 0xF) << 2) | ((channels >> 2) & 1));
        h[3] = (byte)(((channels & 3) << 6) | ((len >> 11) & 3));
        h[4] = (byte)((len >> 3) & 0xFF);
        h[5] = (byte)(((len & 7) << 5) | 0x1F);
        h[6] = 0xFC;
    }
}
