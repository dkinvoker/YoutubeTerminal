using YoutubeTerminal.UI;

namespace YoutubeTerminal.Tests;

/// <summary>
/// Dispatcher interfejsu. Regresja: wynik zadania w tle (np. komentarze) musi oznaczać ekran
/// do przerysowania, a „ciche” zadania muszą być liczone, żeby kręcił się wskaźnik ładowania.
/// </summary>
public class UiDispatcherTests
{
    private static void Drain(UiDispatcher ui, TimeSpan timeout, Func<bool> until)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!until() && DateTime.UtcNow < deadline)
        {
            ui.Wait(20);
            ui.RunPending(ex => throw ex);
        }
    }

    [Fact]
    public void Post_MarksScreenDirtyAndRunsInOrder()
    {
        var ui = new UiDispatcher();
        ui.ConsumeDirty();
        var order = new List<int>();
        ui.Post(() => order.Add(1));
        ui.Post(() => order.Add(2));

        Assert.True(ui.IsDirty);
        Assert.Equal(2, ui.RunPending(_ => { }));
        Assert.Equal([1, 2], order);
        Assert.True(ui.ConsumeDirty());
        Assert.False(ui.ConsumeDirty());
    }

    [Fact]
    public void RunPending_ReportsExceptionsAndContinues()
    {
        var ui = new UiDispatcher();
        var errors = new List<Exception>();
        bool second = false;
        ui.Post(() => throw new InvalidOperationException("x"));
        ui.Post(() => second = true);
        ui.RunPending(errors.Add);
        Assert.Single(errors);
        Assert.True(second);
    }

    [Fact]
    public void QuietTaskCountsAsLoadingAndRedrawsWhenDone()
    {
        var ui = new UiDispatcher();
        var gate = new TaskCompletionSource<int>();
        int result = 0;
        ui.RunAsync(_ => gate.Task, r => result = r, _ => { }, quiet: true, ct: default);

        Assert.Equal(1, ui.InFlight);   // wskaźnik ładowania ma się kręcić...
        Assert.Equal(0, ui.Busy);       // ...choć pasek stanu nie pokazuje „Wczytywanie”

        ui.ConsumeDirty();
        gate.SetResult(42);
        Drain(ui, TimeSpan.FromSeconds(5), () => result != 0);

        Assert.Equal(42, result);
        Assert.Equal(0, ui.InFlight);
        Assert.True(ui.IsDirty);        // wynik pojawia się bez czekania na klawisz
    }

    [Fact]
    public void LoudTaskCountsAsBusy()
    {
        var ui = new UiDispatcher();
        var gate = new TaskCompletionSource<int>();
        ui.RunAsync(_ => gate.Task, _ => { }, _ => { }, quiet: false, ct: default);
        Assert.Equal(1, ui.Busy);
        gate.SetResult(1);
        Drain(ui, TimeSpan.FromSeconds(5), () => ui.Busy == 0);
        Assert.Equal(0, ui.Busy);
    }

    [Fact]
    public void ErrorGoesToErrorHandler()
    {
        var ui = new UiDispatcher();
        Exception? error = null;
        ui.RunAsync<int>(_ => throw new IOException("brak sieci"), _ => { }, ex => error = ex, quiet: true, ct: default);
        Drain(ui, TimeSpan.FromSeconds(5), () => error != null);
        Assert.IsType<IOException>(error);
        Assert.Equal(0, ui.InFlight);
    }

    [Fact]
    public void CancelledTaskCallsNothing()
    {
        var ui = new UiDispatcher();
        using var cts = new CancellationTokenSource();
        bool called = false;
        ui.RunAsync(async ct => { await Task.Delay(Timeout.Infinite, ct); return 1; },
            _ => called = true, _ => called = true, quiet: true, ct: cts.Token);
        cts.Cancel();
        Drain(ui, TimeSpan.FromSeconds(5), () => ui.InFlight == 0);
        Assert.Equal(0, ui.InFlight);
        Assert.False(called);
    }

    [Fact]
    public void Spinner_AdvancesWithTime()
    {
        Assert.Equal(0, Spinner.FrameIndex(0));
        Assert.Equal(1, Spinner.FrameIndex(Spinner.FrameMilliseconds));
        Assert.Equal(0, Spinner.FrameIndex(Spinner.FrameMilliseconds * 10));
        Assert.NotEqual(Spinner.At(0), Spinner.At(Spinner.FrameMilliseconds));
    }
}
