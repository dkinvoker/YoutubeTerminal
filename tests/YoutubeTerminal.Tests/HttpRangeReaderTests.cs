using YoutubeTerminal.Playback;
using YoutubeTerminal.Tests.Helpers;

namespace YoutubeTerminal.Tests;

public class HttpRangeReaderTests : IDisposable
{
    private readonly LocalHttpServer _server = new();
    private readonly byte[] _data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

    public HttpRangeReaderTests() => _server.Add("file.bin", _data);

    public void Dispose() => _server.Dispose();

    [Fact]
    public void ReadsRangeAndLearnsLength()
    {
        var r = new HttpRangeReader(_server.Url("file.bin"));
        Assert.Equal(_data[100..110], r.Read(100, 10, CancellationToken.None));
        Assert.Equal(1000, r.Length);
        Assert.Contains("bytes=100-109", _server.RangeHeaders);
    }

    [Fact]
    public void ClipsAtEndOfFile()
    {
        var r = new HttpRangeReader(_server.Url("file.bin"));
        r.Read(0, 1, CancellationToken.None);
        Assert.Equal(_data[995..], r.Read(995, 100, CancellationToken.None));
        Assert.Empty(r.Read(1000, 10, CancellationToken.None));
        Assert.Empty(r.Read(0, 0, CancellationToken.None));
    }

    [Fact]
    public void BeyondEndWithoutKnownLengthReturnsEmpty()
    {
        var r = new HttpRangeReader(_server.Url("file.bin"));
        Assert.Empty(r.Read(5000, 10, CancellationToken.None)); // serwer odpowiada 416
    }

    [Fact]
    public void ServerIgnoringRangeIsHandled()
    {
        _server.IgnoreRange = true;
        var r = new HttpRangeReader(_server.Url("file.bin"));
        Assert.Equal(_data[10..15], r.Read(10, 5, CancellationToken.None));
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        var r = new HttpRangeReader(_server.Url("file.bin"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => r.Read(0, 10, cts.Token));
    }

    [Fact]
    public void MissingFileFailsAfterRetries()
    {
        var r = new HttpRangeReader(_server.Url("missing.bin"));
        Assert.Throws<IOException>(() => r.Read(0, 10, CancellationToken.None));
        Assert.Equal(4, _server.RequestCount); // 4 próby
    }
}
