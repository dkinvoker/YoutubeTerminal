using YoutubeTerminal.UI;

namespace YoutubeTerminal.Tests;

public class LatestRequestTests
{
    [Fact]
    public void Restart_CancelsPrevious_AndGivesFreshToken()
    {
        // Scenariusz zmiany sortowania komentarzy („o”): pierwsze żądanie trwa, drugie je zastępuje.
        var req = new LatestRequest();
        var (g1, t1) = req.Restart();
        var (g2, t2) = req.Restart();

        Assert.True(t1.IsCancellationRequested);
        Assert.False(t2.IsCancellationRequested);
        Assert.False(req.IsCurrent(g1));
        Assert.True(req.IsCurrent(g2));
    }

    [Fact]
    public void Restart_AfterCancel_IsNotCancelled()
    {
        var req = new LatestRequest();
        req.Restart();
        req.Cancel();
        var (_, t) = req.Restart();
        Assert.False(t.IsCancellationRequested);
    }

    [Fact]
    public void Continue_KeepsGeneration_AndSharesToken()
    {
        var req = new LatestRequest();
        var (g1, t1) = req.Restart();
        var (g2, t2) = req.Continue();
        Assert.Equal(g1, g2);
        Assert.Equal(t1, t2);
        Assert.False(t2.IsCancellationRequested);
    }

    [Fact]
    public async Task StaleResponse_IsIgnored_WhenDispatcherRunsItLate()
    {
        var ui = new UiDispatcher();
        var req = new LatestRequest();
        var results = new List<string>();
        var slow = new TaskCompletionSource<string>();

        var (g1, t1) = req.Restart();
        ui.RunAsync(_ => slow.Task, r => { if (req.IsCurrent(g1)) results.Add(r); }, _ => { }, quiet: true, ct: t1);
        var (g2, t2) = req.Restart();
        ui.RunAsync(_ => Task.FromResult("nowe"), r => { if (req.IsCurrent(g2)) results.Add(r); }, _ => { }, quiet: true, ct: t2);
        slow.SetResult("stare");

        for (int i = 0; i < 50 && (ui.InFlight > 0 || results.Count == 0); i++)
        {
            await Task.Delay(10);
            ui.RunPending(_ => { });
        }
        ui.RunPending(_ => { });
        Assert.Equal(["nowe"], results);
    }
}
