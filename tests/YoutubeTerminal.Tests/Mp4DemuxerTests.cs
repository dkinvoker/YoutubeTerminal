using YoutubeTerminal.Playback;
using YoutubeTerminal.Tests.Helpers;
using static YoutubeTerminal.Tests.Helpers.Mp4Builder;

namespace YoutubeTerminal.Tests;

public class Mp4DemuxerTests : IDisposable
{
    private readonly LocalHttpServer _server = new();

    public void Dispose() => _server.Dispose();

    private const uint Timescale = 15360;

    private static List<List<FragSample>> TwoSegments() =>
    [
        [
            new(AvccNal(0x65, 1, 1), 512, 1024, true),
            new(AvccNal(0x41, 1, 2), 512, 0, false),
            new(AvccNal(0x41, 1, 3, 3), 512, 512, false),
        ],
        [
            new(AvccNal(0x65, 2, 1), 512, 0, true),
            new(AvccNal(0x41, 2, 2), 512, 0, false),
            new(AvccNal(0x41, 2, 3), 512, 0, false),
        ],
    ];

    private Mp4Demuxer OpenFragmented()
    {
        _server.Add("video.mp4", FragmentedVideo(Timescale, 854, 480, TwoSegments()));
        return Mp4Demuxer.Open(_server.Url("video.mp4"), "vide", CancellationToken.None);
    }

    [Fact]
    public void Fragmented_ReadsTrackInfo()
    {
        var d = OpenFragmented();
        Assert.True(d.IsFragmented);
        Assert.Equal("avc1", d.Track.Codec);
        Assert.Equal("vide", d.Track.Handler);
        Assert.Equal((854, 480), (d.Track.Width, d.Track.Height));
        Assert.Equal(Timescale, d.Track.Timescale);
        Assert.Equal(AvcC(), d.Track.AvcConfig);
        Assert.Equal(TimeSpan.FromSeconds(3072.0 / Timescale), d.Duration);
    }

    [Fact]
    public void Fragmented_ReadsAllSamplesWithTimesAndKeyFlags()
    {
        var d = OpenFragmented();
        var samples = d.ReadSamples(TimeSpan.Zero, CancellationToken.None).ToList();
        var expected = TwoSegments().SelectMany(s => s).ToList();

        Assert.Equal(6, samples.Count);
        Assert.Equal([0L, 512, 1024, 1536, 2048, 2560], samples.Select(s => s.Dts));
        Assert.Equal([1024L, 512, 1536, 1536, 2048, 2560], samples.Select(s => s.Pts));
        Assert.Equal([true, false, false, true, false, false], samples.Select(s => s.IsKey));
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i].Data, samples[i].Data.ToArray());
            Assert.Equal(512, samples[i].Duration);
        }
    }

    [Fact]
    public void Fragmented_SeekStartsFromSegmentContainingPosition()
    {
        var d = OpenFragmented();
        var samples = d.ReadSamples(TimeSpan.FromSeconds(2304.0 / Timescale), CancellationToken.None).ToList();
        Assert.Equal(3, samples.Count);
        Assert.Equal(1536, samples[0].Dts);
        Assert.True(samples[0].IsKey);
    }

    [Fact]
    public void Fragmented_UsesRangeRequests()
    {
        var d = OpenFragmented();
        _ = d.ReadSamples(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.All(_server.RangeHeaders, h => Assert.StartsWith("bytes=", h));
        Assert.True(_server.RangeHeaders.Count >= 3); // nagłówek + 2 segmenty
    }

    [Fact]
    public void Fragmented_WrongHandlerThrows()
    {
        _server.Add("v.mp4", FragmentedVideo(Timescale, 640, 360, TwoSegments()));
        var ex = Assert.Throws<InvalidDataException>(() => Mp4Demuxer.Open(_server.Url("v.mp4"), "soun", CancellationToken.None));
        Assert.Contains("audio", ex.Message);
    }

    private static readonly byte[] Asc = [0x12, 0x10];

    private static List<byte[]> AudioSamples() =>
        [Enumerable.Repeat((byte)1, 10).ToArray(), Enumerable.Repeat((byte)2, 11).ToArray(),
         Enumerable.Repeat((byte)3, 12).ToArray(), Enumerable.Repeat((byte)4, 13).ToArray()];

    private Mp4Demuxer OpenProgressive()
    {
        _server.Add("audio.m4a", ProgressiveAudio(44100, 44100, 2, Asc, AudioSamples(), 1024, perChunk: 2));
        return Mp4Demuxer.Open(_server.Url("audio.m4a"), "soun", CancellationToken.None);
    }

    [Fact]
    public void Progressive_ReadsAudioTrackInfo()
    {
        var d = OpenProgressive();
        Assert.False(d.IsFragmented);
        Assert.Equal("mp4a", d.Track.Codec);
        Assert.Equal(44100, d.Track.SampleRate);
        Assert.Equal(2, d.Track.Channels);
        Assert.Equal(Asc, d.Track.AudioConfig);
        Assert.InRange(d.Duration.TotalMilliseconds, 4096 / 44.1 - 0.01, 4096 / 44.1 + 0.01);
    }

    [Fact]
    public void Progressive_ReadsSamplesFromChunks()
    {
        var d = OpenProgressive();
        var samples = d.ReadSamples(TimeSpan.Zero, CancellationToken.None).ToList();
        var expected = AudioSamples();
        Assert.Equal(4, samples.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i], samples[i].Data.ToArray());
            Assert.Equal(i * 1024L, samples[i].Dts);
            Assert.True(samples[i].IsKey);
        }
    }

    [Fact]
    public void Progressive_SeekStartsAtSampleBeforePosition()
    {
        var d = OpenProgressive();
        var samples = d.ReadSamples(TimeSpan.FromSeconds(2100 / 44100.0), CancellationToken.None).ToList();
        Assert.Equal([2048L, 3072], samples.Select(s => s.Dts));
    }

    [Fact]
    public void Mp4Source_ConvertsTimesTo100ns()
    {
        var src = new Mp4Source(OpenProgressive());
        var samples = src.Read(TimeSpan.Zero, CancellationToken.None).ToList();
        Assert.False(src.AnnexB);
        Assert.False(src.IsLive);
        const long frame = 1024L * 10_000_000 / 44100; // 232 199 ticków
        Assert.InRange(samples[1].Time, frame - 2, frame + 2);
        Assert.InRange(samples[0].Duration, frame - 2, frame + 2);
    }

    [Fact]
    public void EmptyStreamThrows()
    {
        _server.Add("empty.mp4", new byte[4]);
        Assert.Throws<InvalidDataException>(() => Mp4Demuxer.Open(_server.Url("empty.mp4"), "vide", CancellationToken.None));
    }
}
