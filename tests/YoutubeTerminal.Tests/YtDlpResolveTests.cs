using YoutubeTerminal.Playback;

namespace YoutubeTerminal.Tests;

/// <summary>
/// Łączenie zapytań do yt-dlp i cache. Prawdziwy proces jest podmieniony (YtDlp.Runner),
/// dlatego testy w tej klasie nie mogą działać równolegle z innymi korzystającymi z Runner.
/// </summary>
[Collection(nameof(YtDlpResolveTests))]
public class YtDlpResolveTests : IDisposable
{
    private readonly Func<string, int, bool, CancellationToken, Task<StreamInfo>> _original = YtDlp.Runner;

    public YtDlpResolveTests() => YtDlp.ClearCache();

    public void Dispose()
    {
        YtDlp.Runner = _original;
        YtDlp.ClearCache();
    }

    private static StreamInfo Info(string id) => new() { VideoId = id, VideoUrl = "v", AudioUrl = "a" };

    [Fact]
    public async Task ConcurrentRequestsShareOneProcess()
    {
        int calls = 0;
        var gate = new TaskCompletionSource();
        YtDlp.Runner = async (id, h, a, ct) => { Interlocked.Increment(ref calls); await gate.Task; return Info(id); };

        var first = YtDlp.ResolveAsync("vid00000001", 480, false, CancellationToken.None);
        var second = YtDlp.ResolveAsync("vid00000001", 480, false, CancellationToken.None);
        Assert.True(YtDlp.IsResolving("vid00000001", 480, false));
        gate.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, calls);
        Assert.False(YtDlp.IsResolving("vid00000001", 480, false));
    }

    [Fact]
    public async Task ResultIsCached()
    {
        int calls = 0;
        YtDlp.Runner = (id, h, a, ct) => { calls++; return Task.FromResult(Info(id)); };
        await YtDlp.ResolveAsync("vid00000002", 480, false, CancellationToken.None);
        await YtDlp.ResolveAsync("vid00000002", 480, false, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.NotNull(YtDlp.TryGetCached("vid00000002", 480, false));
        Assert.Null(YtDlp.TryGetCached("vid00000002", 720, false)); // inna jakość = inny wpis
    }

    [Fact]
    public async Task CancellingOneCallerDoesNotCancelSharedWork()
    {
        var gate = new TaskCompletionSource();
        YtDlp.Runner = async (id, h, a, ct) => { await gate.Task; return Info(id); };
        using var cts = new CancellationTokenSource();

        var cancelled = YtDlp.ResolveAsync("vid00000003", 480, false, cts.Token);
        var other = YtDlp.ResolveAsync("vid00000003", 480, false, CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        Assert.Equal("vid00000003", (await other).VideoId);
    }

    [Fact]
    public async Task FailureIsNotCached()
    {
        int calls = 0;
        YtDlp.Runner = (id, h, a, ct) =>
        {
            calls++;
            return calls == 1 ? Task.FromException<StreamInfo>(new InvalidOperationException("yt-dlp: błąd")) : Task.FromResult(Info(id));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => YtDlp.ResolveAsync("vid00000004", 480, false, CancellationToken.None));
        var ok = await YtDlp.ResolveAsync("vid00000004", 480, false, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal("vid00000004", ok.VideoId);
    }
}
