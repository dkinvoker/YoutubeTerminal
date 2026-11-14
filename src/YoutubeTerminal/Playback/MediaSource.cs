namespace YoutubeTerminal.Playback;

/// <summary>Próbka skompresowanych danych z czasem w jednostkach 100 ns.</summary>
internal readonly struct MediaSample
{
    public readonly byte[] Buffer;
    public readonly int Offset;
    public readonly int Length;
    public readonly long Time;
    public readonly long Duration;
    public readonly bool IsKey;

    public MediaSample(byte[] buffer, int offset, int length, long time, long duration, bool isKey)
    {
        Buffer = buffer; Offset = offset; Length = length; Time = time; Duration = duration; IsKey = isKey;
    }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(Offset, Length);
}

/// <summary>Źródło próbek jednej ścieżki (wideo H.264 lub audio AAC).</summary>
internal interface IMediaSource
{
    string Codec { get; }
    /// <summary>avcC (dla MP4) — null, gdy parametry SPS/PPS są w strumieniu (Annex B).</summary>
    byte[]? AvcConfig { get; }
    byte[]? AudioConfig { get; }
    bool AnnexB { get; }
    int Width { get; }
    int Height { get; }
    int SampleRate { get; }
    int Channels { get; }
    TimeSpan Duration { get; }
    bool IsLive { get; }
    IEnumerable<MediaSample> Read(TimeSpan start, CancellationToken ct);
}

/// <summary>Adapter: ścieżka z pliku MP4 (DASH lub klasyczny).</summary>
internal sealed class Mp4Source : IMediaSource
{
    private readonly Mp4Demuxer _demux;

    public Mp4Source(Mp4Demuxer demux) => _demux = demux;

    private Mp4Track T => _demux.Track;
    public string Codec => T.Codec;
    public byte[]? AvcConfig => T.AvcConfig;
    public byte[]? AudioConfig => T.AudioConfig;
    public bool AnnexB => false;
    public int Width => T.Width;
    public int Height => T.Height;
    public int SampleRate => T.SampleRate;
    public int Channels => T.Channels;
    public TimeSpan Duration => _demux.Duration;
    public bool IsLive => false;

    public IEnumerable<MediaSample> Read(TimeSpan start, CancellationToken ct)
    {
        var t = T;
        foreach (var s in _demux.ReadSamples(start, ct))
            yield return new MediaSample(s.Buffer, s.Offset, s.Length, t.ToTime(s.Pts).Ticks, t.ToTime(s.Duration).Ticks, s.IsKey);
    }
}
