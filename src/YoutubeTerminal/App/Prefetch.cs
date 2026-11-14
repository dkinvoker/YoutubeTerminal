using YoutubeTerminal.Playback;
using YoutubeTerminal.Storage;
using YoutubeTerminal.YouTube;

namespace YoutubeTerminal.UI;

/// <summary>
/// Pobieranie adresów strumieni (yt-dlp) z wyprzedzeniem: gdy zaznaczenie na liście zatrzyma się
/// na filmie, w tle startuje yt-dlp — po naciśnięciu „odtwórz” wynik jest zwykle już gotowy.
/// Naraz działa najwyżej jedno takie pobranie.
/// </summary>
internal static class Prefetch
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1.2);
    private static string? _lastId;
    private static int _running;

    /// <summary>Wywoływane z pętli interfejsu.</summary>
    public static void Tick(View top)
    {
        var list = top.FocusedList;
        if (list?.Current is not VideoItem v) return;
        if (DateTime.UtcNow - list.SelectedAt < Delay) return;
        if (v.Id == _lastId) return;
        if (Start(v)) _lastId = v.Id;
    }

    /// <summary>Uruchamia pobranie (zwraca false, gdy inne właśnie trwa lub film się nie nadaje).</summary>
    public static bool Start(VideoItem v)
    {
        if (v.IsUpcoming || string.IsNullOrEmpty(v.Id)) return true;
        if (YtDlp.FindExecutable() == null) return true;
        int height = App.Current.StreamHeight();
        if (YtDlp.TryGetCached(v.Id, height, false) != null) return true;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        var id = v.Id;
        _ = Task.Run(async () =>
        {
            try { await YtDlp.ResolveAsync(id, height, false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write($"Pobieranie z wyprzedzeniem {id}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
        return true;
    }
}
