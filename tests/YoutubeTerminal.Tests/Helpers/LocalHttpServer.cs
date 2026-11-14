using System.Net;
using System.Net.Sockets;
using System.Text;

namespace YoutubeTerminal.Tests.Helpers;

/// <summary>
/// Minimalny serwer HTTP na 127.0.0.1 (losowy port) do testów pobierania strumieni.
/// Obsługuje GET z nagłówkiem Range (odpowiedź 206 + Content-Range) i zwykły GET (200).
/// </summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private int _requests;

    /// <summary>Gdy true, serwer ignoruje nagłówek Range i zawsze zwraca cały plik (200).</summary>
    public bool IgnoreRange { get; set; }

    public int RequestCount => _requests;
    public List<string> RangeHeaders { get; } = new();

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string path) => $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";

    public void Add(string path, byte[] content)
    {
        lock (_files) _files["/" + path.TrimStart('/')] = content;
    }

    public void Add(string path, string content) => Add(path, Encoding.UTF8.GetBytes(content));

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = await ReadHeader(stream);
                if (header == null) return;
                Interlocked.Increment(ref _requests);
                var lines = header.Split("\r\n");
                var parts = lines[0].Split(' ');
                string path = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Split('?')[0]) : "/";
                string? range = lines.Skip(1)
                    .FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
                if (range != null) lock (RangeHeaders) RangeHeaders.Add(range);

                byte[]? body;
                lock (_files) _files.TryGetValue(path, out body);
                if (body == null)
                {
                    await Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", []);
                    return;
                }

                if (range != null && !IgnoreRange && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var r = range[6..].Split('-');
                    long start = long.Parse(r[0]);
                    long end = r.Length > 1 && r[1].Length > 0 ? long.Parse(r[1]) : body.Length - 1;
                    if (start >= body.Length)
                    {
                        await Write(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{body.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", []);
                        return;
                    }
                    end = Math.Min(end, body.Length - 1);
                    var slice = body.AsSpan((int)start, (int)(end - start + 1)).ToArray();
                    await Write(stream,
                        $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{body.Length}\r\nContent-Length: {slice.Length}\r\nConnection: close\r\n\r\n",
                        slice);
                }
                else
                {
                    await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n", body);
                }
            }
            catch
            {
                // klient mógł się rozłączyć — w testach ignorujemy
            }
        }
    }

    private static async Task<string?> ReadHeader(NetworkStream s)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (buf.Count < 65536)
        {
            int n = await s.ReadAsync(one);
            if (n == 0) return null;
            buf.Add(one[0]);
            int c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray(), 0, c - 4);
        }
        return null;
    }

    private static async Task Write(NetworkStream s, string head, byte[] body)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (body.Length > 0) await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
