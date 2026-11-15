namespace YoutubeTerminal.UI;

/// <summary>
/// Pilnuje, by liczyła się tylko odpowiedź na najnowsze żądanie danego rodzaju (np. komentarze
/// po zmianie sortowania). <see cref="Restart"/> anuluje poprzednie żądania i zawsze wydaje świeży,
/// nieanulowany token; odpowiedzi ze starszej generacji należy odrzucić przez <see cref="IsCurrent"/>.
/// </summary>
internal sealed class LatestRequest
{
    private CancellationTokenSource? _cts;
    private int _generation;

    public int Generation => _generation;

    /// <summary>Nowa seria żądań: anuluje poprzednie i zwraca nową generację z nowym tokenem.</summary>
    public (int Generation, CancellationToken Token) Restart()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return (++_generation, _cts.Token);
    }

    /// <summary>Kolejne żądanie w bieżącej serii (np. następna strona) — bez anulowania poprzednich.</summary>
    public (int Generation, CancellationToken Token) Continue()
    {
        _cts ??= new CancellationTokenSource();
        return (_generation, _cts.Token);
    }

    public bool IsCurrent(int generation) => generation == _generation;

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        _generation++;
    }
}
