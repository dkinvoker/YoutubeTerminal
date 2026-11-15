using System.Diagnostics;
using System.Text;
using System.Text.Json;
using YoutubeTerminal.Storage;

namespace YoutubeTerminal.Playback;

/// <summary>Informacje o strumieniach do odtworzenia.</summary>
internal sealed class StreamInfo
{
    public string VideoId = "";
    public string Title = "";
    public string Channel = "";
    public string ChannelId = "";
    public double Duration;
    public bool IsLive;
    public string? VideoUrl;
    public string? AudioUrl;
    public int Width;
    public int Height;
    public double Fps;
    public string VideoCodec = "";
    public string AudioCodec = "";
    public string FormatNote = "";
    public DateTime ResolvedAt = DateTime.UtcNow;
}

/// <summary>Uruchamia yt-dlp w celu pobrania bezpośrednich adresów strumieni.</summary>
internal static class YtDlp
{
    private static readonly Dictionary<string, StreamInfo> Cache = new();
    private static readonly object CacheLock = new();

    public static string? FindExecutable()
    {
        if (File.Exists(Installer.YtDlpPath)) return Installer.YtDlpPath;
        string local = Path.Combine(AppPaths.ExeDirectory, "yt-dlp.exe");
        if (File.Exists(local)) return local;
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), "yt-dlp.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private static ProcessStartInfo CreateStartInfo(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // PyInstaller rozpakowuje się do TEMP — kierujemy go do naszego folderu, by deinstalator mógł posprzątać.
        psi.Environment["TEMP"] = AppPaths.TempDir;
        psi.Environment["TMP"] = AppPaths.TempDir;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    private static void AddCommonArgs(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("--ignore-config");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(AppPaths.YtDlpCacheDir);
    }

    public static StreamInfo? TryGetCached(string videoId, int maxHeight, bool audioOnly)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(Key(videoId, maxHeight, audioOnly), out var si) && DateTime.UtcNow - si.ResolvedAt < TimeSpan.FromHours(2))
                return si;
        }
        return null;
    }

    private static string Key(string id, int h, bool a) => $"{id}|{h}|{a}";

    private static readonly Dictionary<string, Task<StreamInfo>> InFlight = new();

    /// <summary>Faktyczne uruchomienie yt-dlp (podmieniane w testach).</summary>
    internal static Func<string, int, bool, CancellationToken, Task<StreamInfo>> Runner = RunAsync;

    public static bool IsResolving(string videoId, int maxHeight, bool audioOnly)
    {
        lock (CacheLock) return InFlight.ContainsKey(Key(videoId, maxHeight, audioOnly));
    }

    /// <summary>
    /// Zwraca adresy strumieni. Wynik jest zapamiętywany na 2 h, a równoczesne zapytania o ten sam film
    /// (np. pobieranie z wyprzedzeniem i start odtwarzania) czekają na jeden proces yt-dlp.
    /// Anulowanie <paramref name="ct"/> przerywa tylko oczekiwanie, nie wspólne zadanie.
    /// </summary>
    public static Task<StreamInfo> ResolveAsync(string videoId, int maxHeight, bool audioOnly, CancellationToken ct)
    {
        var cached = TryGetCached(videoId, maxHeight, audioOnly);
        if (cached != null) return Task.FromResult(cached);

        string key = Key(videoId, maxHeight, audioOnly);
        Task<StreamInfo> task;
        lock (CacheLock)
        {
            if (!InFlight.TryGetValue(key, out task!))
            {
                task = ResolveSharedAsync(key, videoId, maxHeight, audioOnly);
                if (!task.IsCompleted) InFlight[key] = task;
            }
        }
        return task.WaitAsync(ct);
    }

    private static async Task<StreamInfo> ResolveSharedAsync(string key, string videoId, int maxHeight, bool audioOnly)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var info = await Runner(videoId, maxHeight, audioOnly, timeout.Token).ConfigureAwait(false);
            lock (CacheLock)
            {
                if (Cache.Count > 50) Cache.Clear();
                Cache[key] = info;
            }
            return info;
        }
        finally
        {
            lock (CacheLock) InFlight.Remove(key);
        }
    }

    internal static void ClearCache()
    {
        lock (CacheLock) Cache.Clear();
    }

    private static async Task<StreamInfo> RunAsync(string videoId, int maxHeight, bool audioOnly, CancellationToken ct)
    {
        string exe = FindExecutable() ?? throw new InvalidOperationException(L.T("Nie znaleziono yt-dlp – odtwarzanie jest niedostępne.", "yt-dlp not found — playback is unavailable."));
        var psi = CreateStartInfo(exe);
        AddCommonArgs(psi);
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("-f");
        int h = Math.Clamp(maxHeight, 144, 2160);
        psi.ArgumentList.Add(audioOnly
            ? "ba[acodec^=mp4a]/ba/b"
            : $"bv*[vcodec^=avc1][height<={h}]+ba[acodec^=mp4a]/b[vcodec^=avc1][height<={h}]/bv*[vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<={h}]+ba/b");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(videoId);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("yt-dlp: " + ExtractError(stderr));

        return Parse(stdout, audioOnly);
    }

    private static string ExtractError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var err = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? L.T("nieznany błąd", "unknown error");
        if (err.StartsWith("ERROR: ", StringComparison.Ordinal)) err = err[7..];
        return err.Length > 300 ? err[..300] : err;
    }

    internal static StreamInfo Parse(string json, bool audioOnly)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new StreamInfo
        {
            VideoId = Str(root, "id"),
            Title = Str(root, "title"),
            Channel = Str(root, "channel") is { Length: > 0 } c ? c : Str(root, "uploader"),
            ChannelId = Str(root, "channel_id"),
            Duration = Num(root, "duration"),
            IsLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
            FormatNote = Str(root, "format_id"),
        };

        if (root.TryGetProperty("requested_formats", out var rf) && rf.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in rf.EnumerateArray())
            {
                string furl = Str(f, "url");
                if (furl.Length == 0) continue;
                string vcodec = Str(f, "vcodec"), acodec = Str(f, "acodec");
                bool hasVideo = vcodec.Length > 0 && vcodec != "none";
                bool hasAudio = acodec.Length > 0 && acodec != "none";
                if (hasVideo && info.VideoUrl == null)
                {
                    info.VideoUrl = furl;
                    info.Width = (int)Num(f, "width");
                    info.Height = (int)Num(f, "height");
                    info.Fps = Num(f, "fps");
                    info.VideoCodec = vcodec;
                }
                if (hasAudio && info.AudioUrl == null)
                {
                    info.AudioUrl = furl;
                    info.AudioCodec = acodec;
                }
            }
        }
        else
        {
            // Pojedynczy format (np. połączony obraz+dźwięk albo samo audio).
            // Nieznany kodek ("" ) traktujemy jak obecny, ale bez adresu nie ma strumienia.
            string url = Str(root, "url");
            string vcodec = Str(root, "vcodec"), acodec = Str(root, "acodec");
            bool hasVideo = url.Length > 0 && vcodec != "none" && !audioOnly;
            bool hasAudio = url.Length > 0 && acodec != "none";
            if (hasVideo)
            {
                info.VideoUrl = url;
                info.Width = (int)Num(root, "width");
                info.Height = (int)Num(root, "height");
                info.Fps = Num(root, "fps");
                info.VideoCodec = vcodec;
            }
            if (hasAudio)
            {
                info.AudioUrl = url;
                info.AudioCodec = acodec;
            }
        }

        if (audioOnly) info.VideoUrl = null;
        if (info.VideoUrl == null && info.AudioUrl == null)
            throw new InvalidOperationException(L.T("yt-dlp nie zwrócił żadnego strumienia.", "yt-dlp returned no streams."));
        return info;
    }

    /// <summary>Aktualizuje wypakowany yt-dlp (yt-dlp -U). Zwraca wynik tekstowy.</summary>
    public static async Task<string> UpdateAsync(CancellationToken ct)
    {
        string exe = FindExecutable() ?? throw new InvalidOperationException(L.T("Nie znaleziono yt-dlp", "yt-dlp not found"));
        var psi = CreateStartInfo(exe);
        psi.ArgumentList.Add("--ignore-config");
        psi.ArgumentList.Add("-U");
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();
        var o = proc.StandardOutput.ReadToEndAsync(ct);
        var e = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string text = ((await o) + "\n" + (await e)).Trim();
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return last;
    }

    public static async Task<string> VersionAsync(CancellationToken ct)
    {
        string? exe = FindExecutable();
        if (exe == null) return L.T("brak", "missing");
        var psi = CreateStartInfo(exe);
        psi.ArgumentList.Add("--version");
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        proc.StandardInput.Close();
        string v = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return v.Trim();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
