using YoutubeTerminal.Playback;
using YoutubeTerminal.Tests.Helpers;

namespace YoutubeTerminal.Tests;

public class HlsTests : IDisposable
{
    private readonly LocalHttpServer _server = new();

    public void Dispose() => _server.Dispose();

    private static string Playlist(long firstSeq, IEnumerable<string> segments, bool ended = true) =>
        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n" +
        $"#EXT-X-MEDIA-SEQUENCE:{firstSeq}\n" +
        string.Concat(segments.Select(s => $"#EXTINF:2.0,\n{s}\n")) +
        (ended ? "#EXT-X-ENDLIST\n" : "");

    [Fact]
    public async Task Playlist_ParsesSequenceDurationsAndUrls()
    {
        _server.Add("live/index.m3u8",
            "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:100\n" +
            "#EXTINF:5.0,\nseg100.ts\n#EXTINF:4.5,\n" + _server.Url("abs/seg101.ts") + "\n#EXT-X-ENDLIST\n");
        var pl = await HlsPlaylist.LoadAsync(_server.Url("live/index.m3u8"), CancellationToken.None);

        Assert.Equal(100, pl.MediaSequence);
        Assert.Equal(5, pl.TargetDuration);
        Assert.True(pl.Ended);
        Assert.Equal(2, pl.Segments.Count);
        Assert.Equal((100L, _server.Url("live/seg100.ts"), 5.0), pl.Segments[0]);
        Assert.Equal((101L, _server.Url("abs/seg101.ts"), 4.5), pl.Segments[1]);
    }

    [Fact]
    public void Shared_TimelineStartsWith30SecondMargin()
    {
        var shared = new HlsShared();
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, shared.ToTicks(900_000));
        Assert.Equal(TimeSpan.FromSeconds(31).Ticks, shared.ToTicks(990_000));
        Assert.Equal(TimeSpan.FromSeconds(29).Ticks, shared.ToTicks(810_000)); // druga ścieżka może zacząć wcześniej
    }

    [Fact]
    public void Shared_StartSequenceIsChosenOnce()
    {
        var shared = new HlsShared();
        Assert.Equal(7, shared.GetStart(() => 7));
        Assert.Equal(7, shared.GetStart(() => 99));
    }

    private static readonly byte[] Idr = [0, 0, 0, 1, 0x67, 0x64, 0x00, 0x1F, 0, 0, 0, 1, 0x68, 0xEB, 0, 0, 0, 1, 0x65, 0x88, 0x84];
    private static readonly byte[] PFrame = [0, 0, 0, 1, 0x41, 0x9A, 0x12];

    [Fact]
    public void Video_DemuxesTsAccessUnits()
    {
        _server.Add("v/seg7.ts", TsBuilder.Segment(
            (TsBuilder.VideoPid, 0x1B, Idr, 900_000),
            (TsBuilder.VideoPid, 0x1B, PFrame, 903_003)));
        _server.Add("v/index.m3u8", Playlist(7, ["seg7.ts"]));

        var src = new HlsVideoSource(_server.Url("v/index.m3u8"), new HlsShared(), 640, 360);
        var samples = src.Read(TimeSpan.Zero, CancellationToken.None).ToList();

        Assert.True(src.AnnexB);
        Assert.True(src.IsLive);
        Assert.Null(src.AvcConfig);
        Assert.Equal(2, samples.Count);
        Assert.Equal(Idr, samples[0].Data.ToArray());
        Assert.Equal(PFrame, samples[1].Data.ToArray());
        Assert.True(samples[0].IsKey);
        Assert.False(samples[1].IsKey);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, samples[0].Time);
        Assert.Equal(3003L * 1000 / 9, samples[1].Time - samples[0].Time);
        Assert.Equal(3003L * 1000 / 9, samples[0].Duration);
    }

    [Fact]
    public void Video_LargeAccessUnitSpansManyPackets()
    {
        var big = new byte[5000];
        new Random(3).NextBytes(big);
        byte[] au = [0, 0, 0, 1, 0x65, .. big];
        _server.Add("big/s0.ts", TsBuilder.Segment((TsBuilder.VideoPid, 0x1B, au, 90_000)));
        _server.Add("big/index.m3u8", Playlist(0, ["s0.ts"]));

        var samples = new HlsVideoSource(_server.Url("big/index.m3u8"), new HlsShared(), 0, 0)
            .Read(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.Equal(au, Assert.Single(samples).Data.ToArray());
    }

    [Fact]
    public void Video_StartsThreeSegmentsBeforeEdgeAndSharesStartWithAudio()
    {
        var shared = new HlsShared();
        for (int i = 0; i < 5; i++)
            _server.Add($"edge/v{i}.ts", TsBuilder.Segment((TsBuilder.VideoPid, 0x1B, Idr, 90_000L * (10 + 2 * i))));
        _server.Add("edge/v.m3u8", Playlist(0, Enumerable.Range(0, 5).Select(i => $"v{i}.ts")));

        var video = new HlsVideoSource(_server.Url("edge/v.m3u8"), shared, 0, 0);
        var samples = video.Read(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.Equal(2, video.StartSequence);
        Assert.Equal(3, samples.Count);

        // druga ścieżka z tym samym HlsShared zaczyna od tego samego numeru, choć jej lista jest dłuższa
        for (int i = 0; i < 6; i++)
            _server.Add($"edge/a{i}.aac", [.. TsBuilder.Id3Timestamp(90_000L * (10 + 2 * i)), .. TsBuilder.Adts([1, 2, 3])]);
        _server.Add("edge/a.m3u8", Playlist(0, Enumerable.Range(0, 6).Select(i => $"a{i}.aac")));
        var audio = new HlsAudioSource(_server.Url("edge/a.m3u8"), shared);
        var audioSamples = audio.Read(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.Equal(2, audio.StartSequence);
        Assert.Equal(4, audioSamples.Count);
    }

    [Fact]
    public void Audio_PackedAdtsWithId3Timestamp()
    {
        byte[] p1 = [1, 2, 3], p2 = [4, 5], p3 = [6, 7, 8, 9];
        _server.Add("a/s0.aac", [.. TsBuilder.Id3Timestamp(900_000), .. TsBuilder.Adts(p1), .. TsBuilder.Adts(p2), .. TsBuilder.Adts(p3)]);
        _server.Add("a/index.m3u8", Playlist(0, ["s0.aac"]));

        var src = new HlsAudioSource(_server.Url("a/index.m3u8"), new HlsShared());
        var samples = src.Read(TimeSpan.Zero, CancellationToken.None).ToList();

        Assert.Equal([0x12, 0x10], src.AudioConfig);
        Assert.Equal(44100, src.SampleRate);
        Assert.Equal(2, src.Channels);
        Assert.Equal([p1, p2, p3], samples.Select(s => s.Data.ToArray()));
        Assert.All(samples, s => Assert.True(s.IsKey));
        long frameTicks = 1024L * 10_000_000 / 44100;
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, samples[0].Time);
        Assert.InRange(samples[1].Time - samples[0].Time, frameTicks - 200, frameTicks + 200);
        Assert.Equal(frameTicks, samples[0].Duration);
    }

    [Fact]
    public void Audio_AacInsideTransportStream()
    {
        byte[] pes = [.. TsBuilder.Adts([9, 9]), .. TsBuilder.Adts([8, 8, 8])];
        _server.Add("ta/s0.ts", TsBuilder.Segment((TsBuilder.AudioPid, 0x0F, pes, 180_000)));
        _server.Add("ta/index.m3u8", Playlist(0, ["s0.ts"]));

        var samples = new HlsAudioSource(_server.Url("ta/index.m3u8"), new HlsShared())
            .Read(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.Equal([new byte[] { 9, 9 }, [8, 8, 8]], samples.Select(s => s.Data.ToArray()));
    }

    [Fact]
    public void Audio_ProbeReadsCodecParameters()
    {
        _server.Add("p/s0.aac", [.. TsBuilder.Id3Timestamp(0), .. TsBuilder.Adts([1], freqIdx: 3, channels: 1)]);
        _server.Add("p/index.m3u8", Playlist(0, ["s0.aac"]));

        var src = new HlsAudioSource(_server.Url("p/index.m3u8"), new HlsShared());
        src.Probe(CancellationToken.None);
        Assert.Equal(48000, src.SampleRate);
        Assert.Equal(1, src.Channels);
        Assert.Equal([0x11, 0x88], src.AudioConfig); // AAC-LC, 48 kHz, mono
    }
}
