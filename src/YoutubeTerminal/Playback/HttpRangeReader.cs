using System.Net;
using System.Net.Http.Headers;

namespace YoutubeTerminal.Playback;

/// <summary>
/// Losowy dostęp do zasobu HTTP przez żądania Range. YouTube mocno dławi pobieranie całego pliku
/// jednym żądaniem, natomiast krótkie fragmenty z nagłówkiem Range są serwowane z pełną prędkością.
/// </summary>
internal sealed class HttpRangeReader
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Współdzielony klient do pobierania strumieni (segmenty HLS itp.).</summary>
    public static HttpClient Shared => Http;

    public string Url { get; }
    public long Length { get; private set; } = -1;

    public HttpRangeReader(string url)
    {
        Url = url;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>Czyta dokładnie <paramref name="count"/> bajtów od <paramref name="offset"/> (mniej tylko na końcu pliku).</summary>
    public byte[] Read(long offset, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        if (Length >= 0)
        {
            if (offset >= Length) return [];
            count = (int)Math.Min(count, Length - offset);
        }

        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                req.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
                using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    return [];
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new HttpRequestException("Serwer odmówił dostępu do strumienia (403) – link mógł wygasnąć.", null, resp.StatusCode);
                resp.EnsureSuccessStatusCode();

                var cr = resp.Content.Headers.ContentRange;
                if (cr?.Length is long total) Length = total;

                using var stream = resp.Content.ReadAsStream(ct);
                if (resp.StatusCode == HttpStatusCode.OK && offset > 0)
                {
                    // Serwer zignorował Range — przewijamy ręcznie (rzadkie).
                    SkipBytes(stream, offset, ct);
                }

                var buf = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int n = stream.Read(buf, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < count) Array.Resize(ref buf, read);
                return buf;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
                Thread.Sleep(300 * (attempt + 1));
            }
        }
        throw new IOException("Błąd pobierania strumienia: " + last?.Message, last);
    }

    private static void SkipBytes(Stream s, long count, CancellationToken ct)
    {
        var tmp = new byte[81920];
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = s.Read(tmp, 0, (int)Math.Min(tmp.Length, count));
            if (n <= 0) break;
            count -= n;
        }
    }
}
