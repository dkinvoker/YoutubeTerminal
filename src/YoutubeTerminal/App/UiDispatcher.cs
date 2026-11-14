using System.Collections.Concurrent;

namespace YoutubeTerminal.UI;

/// <summary>
/// Kolejka akcji do wykonania w wątku interfejsu, flaga „ekran do przerysowania”
/// i licznik zadań w tle. Każda akcja przekazana z tła oznacza ekran do przerysowania —
/// wynik zadania (np. wczytane komentarze) pojawia się od razu, bez czekania na klawisz.
/// </summary>
internal sealed class UiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _dirty = true;
    private int _inFlight;
    private int _busy;

    /// <summary>Ekran wymaga przerysowania.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Wszystkie zadania w tle (także „ciche”, nie pokazywane w pasku stanu).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Zadania „głośne” — pokazywane w pasku stanu.</summary>
    public int Busy => Volatile.Read(ref _busy);

    public int PendingCount => _queue.Count;

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        _dirty = true;
        _wake.Set();
    }

    public void Invalidate()
    {
        _dirty = true;
        _wake.Set();
    }

    public void Wake() => _wake.Set();

    /// <summary>Czeka na zdarzenie (akcja, wejście, odświeżenie) lub upływ czasu.</summary>
    public bool Wait(int milliseconds) => _wake.WaitOne(milliseconds);

    /// <summary>Wykonuje oczekujące akcje (w kolejności). Wyjątki przekazuje do <paramref name="onError"/>.</summary>
    public int RunPending(Action<Exception> onError)
    {
        int n = 0;
        while (_queue.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { onError(ex); }
            n++;
        }
        return n;
    }

    /// <summary>Zwraca true i czyści flagę, jeśli ekran wymagał przerysowania.</summary>
    public bool ConsumeDirty()
    {
        if (!_dirty) return false;
        _dirty = false;
        return true;
    }

    /// <summary>
    /// Wykonuje pracę w tle; wynik (lub błąd) trafia do wątku interfejsu przez <see cref="Post"/>.
    /// Anulowanie przez <paramref name="ct"/> pomija oba wywołania zwrotne.
    /// </summary>
    public void RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> onDone, Action<Exception> onError,
        bool quiet, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        if (!quiet) Interlocked.Increment(ref _busy);
        Invalidate();
        Task.Run(async () =>
        {
            try
            {
                var result = await work(ct).ConfigureAwait(false);
                Post(() =>
                {
                    Finish(quiet);
                    if (!ct.IsCancellationRequested) onDone(result);
                });
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    Finish(quiet);
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) return;
                    onError(ex);
                });
            }
        });
    }

    private void Finish(bool quiet)
    {
        Interlocked.Decrement(ref _inFlight);
        if (!quiet) Interlocked.Decrement(ref _busy);
    }
}

/// <summary>Wskaźnik ładowania liczony z zegara — kręci się niezależnie od rodzaju zadania.</summary>
internal static class Spinner
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    public const int FrameMilliseconds = 90;

    public static int FrameIndex(long milliseconds) => (int)(milliseconds / FrameMilliseconds % Frames.Length);

    public static string At(long milliseconds) => Frames[FrameIndex(milliseconds)].ToString();
}
