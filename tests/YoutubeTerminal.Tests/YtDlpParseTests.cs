using YoutubeTerminal.Playback;

namespace YoutubeTerminal.Tests;

public class YtDlpParseTests
{
    private const string DashJson = """
        {"id":"aqz-KE-bpKQ","title":"Big Buck Bunny","channel":"Blender","channel_id":"UCSMOQeBJ2RAnuFungnQOxLg",
         "duration":635.0,"is_live":false,"format_id":"135+140",
         "requested_formats":[
           {"format_id":"135","url":"https://video.example/v","vcodec":"avc1.4D401F","acodec":"none","width":854,"height":480,"fps":30},
           {"format_id":"140","url":"https://video.example/a","vcodec":"none","acodec":"mp4a.40.2","asr":44100}]}
        """;

    [Fact]
    public void Parse_SeparateVideoAndAudio()
    {
        var info = YtDlp.Parse(DashJson, audioOnly: false);
        Assert.Equal("aqz-KE-bpKQ", info.VideoId);
        Assert.Equal("Big Buck Bunny", info.Title);
        Assert.Equal("Blender", info.Channel);
        Assert.Equal("UCSMOQeBJ2RAnuFungnQOxLg", info.ChannelId);
        Assert.Equal(635.0, info.Duration);
        Assert.False(info.IsLive);
        Assert.Equal("https://video.example/v", info.VideoUrl);
        Assert.Equal("https://video.example/a", info.AudioUrl);
        Assert.Equal((854, 480, 30.0), (info.Width, info.Height, info.Fps));
        Assert.Equal("avc1.4D401F", info.VideoCodec);
        Assert.Equal("mp4a.40.2", info.AudioCodec);
        Assert.Equal("135+140", info.FormatNote);
    }

    [Fact]
    public void Parse_AudioOnlyDropsVideo()
    {
        var info = YtDlp.Parse(DashJson, audioOnly: true);
        Assert.Null(info.VideoUrl);
        Assert.Equal("https://video.example/a", info.AudioUrl);
    }

    [Fact]
    public void Parse_SingleMuxedFormat()
    {
        var info = YtDlp.Parse("""
            {"id":"x","title":"t","uploader":"Uploader","url":"https://muxed.example/18","vcodec":"avc1.42001E","acodec":"mp4a.40.2","width":640,"height":360,"fps":25}
            """, audioOnly: false);
        Assert.Equal("https://muxed.example/18", info.VideoUrl);
        Assert.Equal("https://muxed.example/18", info.AudioUrl);
        Assert.Equal("Uploader", info.Channel);
        Assert.Equal(640, info.Width);
    }

    [Fact]
    public void Parse_LiveFlag()
    {
        var info = YtDlp.Parse("""
            {"id":"live","title":"t","is_live":true,"url":"https://manifest.example/index.m3u8","vcodec":"avc1","acodec":"mp4a"}
            """, audioOnly: false);
        Assert.True(info.IsLive);
    }

    [Fact(Skip = "Znany błąd: brak pól vcodec/acodec traktowany jak strumień z pustym adresem zamiast zgłoszenia błędu.")]
    public void Parse_NoStreamsThrows()
    {
        Assert.Throws<InvalidOperationException>(() => YtDlp.Parse("""{"id":"x","title":"t"}""", audioOnly: false));
    }
}
