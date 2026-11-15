using System.Collections.Concurrent;
using StbImageSharp;
using YoutubeTerminal.Tui;

namespace YoutubeTerminal.Graphics;

/// <summary>
/// Pobieranie, dekodowanie (JPEG/PNG) i kodowanie miniatur do Sixel/półbloków — asynchronicznie, z cache LRU.
/// </summary>
internal sealed class ImageService
{
    private readonly HttpClient _http;
    private readonly Action _onReady;
    private readonly Lru<string, Bitmap?> _bitmaps = new(160);
    private readonly Lru<string, byte[]> _encoded = new(400);
    private readonly ConcurrentDictionary<string, bool> _pending = new();
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly SemaphoreSlim _encodes = new(Math.Max(2, Environment.ProcessorCount / 2));

    [ThreadStatic] private static ImageEncoder? _encoder;

    public RenderMode Mode { get; set; } = RenderMode.Sixel;
    public int CellWidth { get; set; } = 10;
    public int CellHeight { get; set; } = 20;
    public int Background { get; set; } = 0x0F0F0F;

    public ImageService(HttpClient http, Action onReady)
    {
        _http = http;
        _onReady = onReady;
    }

    public void ClearEncoded() => _encoded.Clear();

    /// <summary>
    /// Zwraca umieszczenie obrazu w obszarze lub null (obraz jeszcze się ładuje — wtedy po załadowaniu
    /// zostanie wywołane odświeżenie). <paramref name="fallbackUrl"/> używany, gdy pierwszy adres nie istnieje.
    /// </summary>
    public ImagePlacement? Get(string url, Rect area, string? fallbackUrl = null, bool cover = false)
    {
        if (string.IsNullOrEmpty(url) || area.W <= 0 || area.H <= 0) return null;
        if (!_bitmaps.TryGet(url, out var bmp))
        {
            StartDownload(url, fallbackUrl);
            return null;
        }
        if (bmp == null)
        {
            // Nie udało się pobrać — próbujemy zapasowego adresu.
            if (fallbackUrl != null && fallbackUrl != url) return Get(fallbackUrl, area, null, cover);
            return null;
        }

        string key = $"{url}|{area.X},{area.Y},{area.W},{area.H}|{Mode}|{CellWidth}x{CellHeight}|{cover}";
        if (_encoded.TryGet(key, out var data))
            return new ImagePlacement { Area = area, Data = data, Key = key };

        if (_pending.TryAdd(key, true))
        {
            var mode = Mode;
            int cw = CellWidth, ch = CellHeight;
            Task.Run(async () =>
            {
                await _encodes.WaitAsync().ConfigureAwait(false);
                try
                {
                    var enc = _encoder ??= new ImageEncoder();
                    enc.Mode = mode;
                    enc.CellWidth = cw;
                    enc.CellHeight = ch;
                    enc.AdaptivePalette = true;
                    enc.Dither = false;
                    byte[] bytes;
                    if (cover)
                    {
                        var (pw, ph) = enc.PixelSize(area);
                        var scaled = bmp.CoverInto(pw, ph);
                        bytes = enc.Encode(scaled, area);
                    }
                    else
                    {
                        var layout = enc.Layout(bmp.Width, bmp.Height, area);
                        var scaled = Scaler.Resize(bmp, layout.PixelWidth, layout.PixelHeight);
                        bytes = enc.Encode(scaled, layout.Cells);
                    }
                    _encoded.Set(key, bytes);
                }
                catch
                {
                    // ignorujemy — obraz po prostu się nie pojawi
                }
                finally
                {
                    _encodes.Release();
                    _pending.TryRemove(key, out _);
                }
                _onReady();
            });
        }
        return null;
    }

    /// <summary>Zwraca zdekodowaną bitmapę (lub null, jeśli jeszcze się ładuje).</summary>
    public Bitmap? GetBitmap(string url)
    {
        if (_bitmaps.TryGet(url, out var bmp)) return bmp;
        StartDownload(url, null);
        return null;
    }

    private void StartDownload(string url, string? fallbackUrl)
    {
        if (!_pending.TryAdd("dl|" + url, true)) return;
        Task.Run(async () =>
        {
            await _downloads.WaitAsync().ConfigureAwait(false);
            Bitmap? bmp = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    bmp = Decode(bytes);
                }
            }
            catch
            {
                bmp = null;
            }
            finally
            {
                _downloads.Release();
            }
            _bitmaps.Set(url, bmp);
            _pending.TryRemove("dl|" + url, out _);
            if (bmp == null && fallbackUrl != null) StartDownload(fallbackUrl, null);
            _onReady();
        });
    }

    public Bitmap? Decode(byte[] bytes)
    {
        try
        {
            var img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            var bmp = Bitmap.FromRgba(img.Data, img.Width, img.Height, 0xFF000000u | (uint)Background);
            // Duże obrazy od razu zmniejszamy (oszczędność pamięci i czasu kodowania).
            if (bmp.Width > 1280) bmp = bmp.FitInto(1280, 1280);
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Prosty, bezpieczny wątkowo cache LRU.</summary>
internal sealed class Lru<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();

    public Lru(int capacity) => _capacity = capacity;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                node.Value = (key, value);
                _list.AddFirst(node);
                return;
            }
            var n = _list.AddFirst((key, value));
            _map[key] = n;
            while (_map.Count > _capacity && _list.Last is { } last)
            {
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_map)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
