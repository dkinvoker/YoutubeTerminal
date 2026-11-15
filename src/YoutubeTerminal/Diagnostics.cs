using System.Diagnostics;
using YoutubeTerminal.Graphics;
using YoutubeTerminal.Playback;
using YoutubeTerminal.Storage;

namespace YoutubeTerminal;

/// <summary>Polecenia diagnostyczne uruchamiane z linii poleceń (bez interfejsu TUI).</summary>
internal static class Diagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        switch (args[0])
        {
            case "--diag-sixel":
                return await DiagSixel(args.Length > 1 ? args[1] : "aqz-KE-bpKQ", args.Length > 2 ? args[2] : Path.GetTempPath());
            case "--diag-yt":
                return await DiagYt(args.Length > 1 ? args[1] : "lofi hip hop");
            case "--diag-decode":
                return await DiagDecode(args.Length > 1 ? args[1] : "aqz-KE-bpKQ", args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0);
            default:
                Console.Error.WriteLine("Nieznane polecenie diagnostyczne.");
                return 2;
        }
    }

    private static async Task<int> DiagDecode(string id, double startSec)
    {
        Installer.Run();
        var sw = Stopwatch.StartNew();
        var info = await YtDlp.ResolveAsync(id, 480, false, CancellationToken.None);
        Console.WriteLine($"yt-dlp: {sw.ElapsedMilliseconds} ms, {info.Title}, {info.Width}x{info.Height}@{info.Fps} {info.VideoCodec}/{info.AudioCodec}");
        if (!OperatingSystem.IsWindows()) Console.WriteLine($"ffmpeg: {Ffmpeg.Find() ?? "-"}");

        var start = TimeSpan.FromSeconds(startSec);
        sw.Restart();
        var vd = Mp4Demuxer.Open(info.VideoUrl!, "vide", CancellationToken.None);
        var vt = vd.Track;
        Console.WriteLine($"demux video: {sw.ElapsedMilliseconds} ms, {vt.Codec} {vt.Width}x{vt.Height} ts={vt.Timescale} frag={vd.IsFragmented} dur={vd.Duration}");

        var video = new Mp4Source(vd);
        int frames = 0, samples = 0;
        long firstTs = -1, lastTs = 0;
        uint centre = 0, corner = 0;
        var small = new uint[160 * 90];
        sw.Restart();
        using (var dec = Codecs.CreateVideo(video, 160, 90, _ => true, (ts, px) =>
        {
            Interlocked.Increment(ref frames);
            if (firstTs < 0) firstTs = ts.Ticks;
            lastTs = ts.Ticks;
            if (frames == 30) { px.AsSpan(0, small.Length).CopyTo(small); centre = px[45 * 160 + 80]; corner = px[5 * 160 + 5]; }
        }))
        {
            foreach (var s in video.Read(start, CancellationToken.None))
            {
                dec.Decode(s);
                if (++samples >= 150) break;
            }
            dec.EndOfStream();
        }
        Console.WriteLine($"H.264: {samples} próbek -> {frames} klatek w {sw.ElapsedMilliseconds} ms; ts {TimeSpan.FromTicks(firstTs)}..{TimeSpan.FromTicks(lastTs)}");
        Console.WriteLine($"   piksel środka klatki 30: 0x{centre:X8}, rogu: 0x{corner:X8}");
        SavePpm(small, 160, 90, Path.Combine(Path.GetTempPath(), "yt_frame.ppm"));

        sw.Restart();
        var ad = Mp4Demuxer.Open(info.AudioUrl!, "soun", CancellationToken.None);
        var at = ad.Track;
        Console.WriteLine($"demux audio: {sw.ElapsedMilliseconds} ms, {at.Codec} {at.SampleRate} Hz {at.Channels} ch asc={Convert.ToHexString(at.AudioConfig ?? [])} frag={ad.IsFragmented} dur={ad.Duration}");
        var audio = new Mp4Source(ad);
        long bytes = 0, first = -1;
        int rate = 0, channels = 0, n = 0;
        sw.Restart();
        using (var adec = Codecs.CreateAudio(audio, (pcm, time, r, ch) =>
        {
            bytes += pcm.Length;
            if (first < 0) first = time;
            rate = r; channels = ch;
        }))
        {
            foreach (var s in audio.Read(start, CancellationToken.None))
            {
                adec.Decode(s);
                if (++n >= 500) break;
            }
            adec.EndOfStream();
        }
        Console.WriteLine($"AAC: {n} próbek -> {bytes} B PCM ({bytes / (double)Math.Max(1, rate * channels * 2):F2} s) {rate} Hz {channels} ch w {sw.ElapsedMilliseconds} ms, start {TimeSpan.FromTicks(first)}");
        return 0;
    }

    private static string Describe(YouTube.YtItem i) => i switch
    {
        YouTube.VideoItem v => $"[V{(v.IsShort ? "/short" : "")}{(v.IsLive ? "/live" : "")}] {v.Id} | {v.Title} | {v.ChannelName} ({v.ChannelId}) | {v.DurationText} | {v.ViewsText} | {v.PublishedText}",
        YouTube.ChannelItem c => $"[C] {c.Id} | {c.Title} | {c.Handle} | {c.SubscribersText} | {c.VideoCountText} | av={c.AvatarUrl.Length > 0}",
        YouTube.PlaylistItem p => $"[P] {p.Id} | {p.Title} | {p.ChannelName} ({p.ChannelId}) | {p.VideoCountText} | first={p.FirstVideoId} | th={p.ThumbnailUrl.Length > 0}",
        _ => i.ToString() ?? "",
    };

    private static async Task<int> DiagYt(string query)
    {
        var yt = new YouTube.YouTubeClient();
        var ct = CancellationToken.None;
        var sw = Stopwatch.StartNew();
        var page = await yt.SearchAsync(query, YouTube.SearchFilter.All, YouTube.SearchSort.Relevance, ct);
        Console.WriteLine($"== search '{query}': {page.Items.Count} items, cont={page.Continuation != null} ({sw.ElapsedMilliseconds} ms)");
        foreach (var i in page.Items.Take(12)) Console.WriteLine("  " + Describe(i));
        if (page.Continuation != null)
        {
            var more = await yt.SearchContinuationAsync(page.Continuation, ct);
            Console.WriteLine($"== continuation: {more.Items.Count} items, cont={more.Continuation != null}");
            foreach (var i in more.Items.Take(3)) Console.WriteLine("  " + Describe(i));
        }
        foreach (var f in new[] { YouTube.SearchFilter.Channels, YouTube.SearchFilter.Playlists })
        {
            var fp = await yt.SearchAsync(query, f, YouTube.SearchSort.Relevance, ct);
            Console.WriteLine($"== search filter {f}: {fp.Items.Count}");
            foreach (var i in fp.Items.Take(3)) Console.WriteLine("  " + Describe(i));
        }

        var video = page.Items.OfType<YouTube.VideoItem>().First(v => !v.IsShort);
        var d = await yt.GetVideoAsync(video.Id, ct);
        Console.WriteLine($"== next {video.Id}: '{d.Video.Title}' ch='{d.Video.ChannelName}' ({d.Video.ChannelId}) subs='{d.ChannelSubscribers}' views='{d.ViewsFull}'/'{d.Video.ViewsText}' date='{d.DateText}' likes='{d.LikesText}' comments='{d.CommentsCount}' token={d.CommentsToken != null} chapters={d.Chapters.Count} related={d.Related.Items.Count} relcont={d.Related.Continuation != null}");
        Console.WriteLine("   desc: " + Tui.TextUtil.Truncate(Tui.TextUtil.OneLine(d.Description), 150));
        foreach (var i in d.Related.Items.Take(4)) Console.WriteLine("  " + Describe(i));
        if (d.CommentsToken != null)
        {
            var cp = await yt.GetCommentsAsync(d.CommentsToken, ct);
            Console.WriteLine($"== comments: {cp.Comments.Count} header='{cp.HeaderCount}' sorts={cp.Sorts.Count} cont={cp.Continuation != null}");
            foreach (var c in cp.Comments.Take(3)) Console.WriteLine($"  {c.Author} [{c.Published}] likes={c.Likes} replies={c.ReplyCount}/{c.RepliesToken != null}: {Tui.TextUtil.Truncate(Tui.TextUtil.OneLine(c.Text), 80)}");
            var withReplies = cp.Comments.FirstOrDefault(c => c.RepliesToken != null);
            if (withReplies != null)
            {
                var rp = await yt.GetCommentsAsync(withReplies.RepliesToken!, ct);
                Console.WriteLine($"== replies: {rp.Comments.Count}");
                foreach (var c in rp.Comments.Take(2)) Console.WriteLine($"  ↳ {c.Author}: {Tui.TextUtil.Truncate(Tui.TextUtil.OneLine(c.Text), 70)}");
            }
        }

        var chId = d.Video.ChannelId;
        var ch = await yt.GetChannelAsync(chId, YouTube.YouTubeClient.VideosTabParams, ct);
        Console.WriteLine($"== channel {chId}: '{ch.Channel.Title}' {ch.Channel.Handle} subs='{ch.Channel.SubscribersText}' videos='{ch.Channel.VideoCountText}' banner={ch.BannerUrl.Length > 0} tabs=[{string.Join(", ", ch.Tabs.Select(t => t.Title + (t.Selected ? "*" : "")))}] sorts=[{string.Join(", ", ch.Sorts.Select(s => s.Title))}] items={ch.Content.Items.Count} cont={ch.Content.Continuation != null}");
        foreach (var i in ch.Content.Items.Take(3)) Console.WriteLine("  " + Describe(i));
        var plTab = ch.Tabs.FirstOrDefault(t => t.Title.StartsWith("Playlist", StringComparison.OrdinalIgnoreCase));
        if (plTab != null)
        {
            var chp = await yt.GetChannelAsync(chId, plTab.Params, ct);
            Console.WriteLine($"== channel playlists: {chp.Content.Items.Count}");
            foreach (var i in chp.Content.Items.Take(3)) Console.WriteLine("  " + Describe(i));
            if (chp.Content.Items.OfType<YouTube.PlaylistItem>().FirstOrDefault() is { } pl)
            {
                var pp = await yt.GetPlaylistAsync(pl.Id, pl.FirstVideoId, ct);
                Console.WriteLine($"== playlist {pl.Id}: '{pp.Playlist.Title}' by '{pp.Playlist.ChannelName}' count='{pp.Playlist.VideoCountText}' views='{pp.ViewsText}' items={pp.Content.Items.Count} cont={pp.Content.Continuation != null}");
                foreach (var i in pp.Content.Items.Take(3)) Console.WriteLine("  " + Describe(i));
            }
        }
        var feed = await yt.GetChannelFeedAsync(chId, ch.Channel.Title, ct);
        Console.WriteLine($"== rss: {feed.Count}");
        foreach (var i in feed.Take(2)) Console.WriteLine("  " + Describe(i));
        var sug = await yt.SuggestAsync(query, ct);
        Console.WriteLine($"== suggest: {string.Join(" | ", sug.Take(6))}");
        var r = await yt.ResolveAsync("https://www.youtube.com/@LofiGirl", ct);
        Console.WriteLine($"== resolve @LofiGirl: {r}");
        return 0;
    }

    // ---------------- Test enkodera Sixel ----------------

    private static async Task<int> DiagSixel(string id, string outDir)
    {
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync($"https://i.ytimg.com/vi/{id}/hq720.jpg");
        var svc = new ImageService(http, () => { });
        var src = svc.Decode(bytes)!;
        Console.WriteLine($"miniatura: {src.Width}x{src.Height}");
        var scaled = src.FitInto(640, 360);
        SavePng(scaled.Pixels, scaled.Width, scaled.Height, Path.Combine(outDir, "sixel_src.png"));

        foreach (var (name, adaptive, dither) in new[] { ("adaptive", true, false), ("fixed", false, false), ("dither", false, true) })
        {
            var enc = new ImageEncoder { Mode = RenderMode.Sixel, AdaptivePalette = adaptive, Dither = dither };
            var sw = Stopwatch.StartNew();
            byte[] data = [];
            for (int i = 0; i < 10; i++) data = enc.Encode(scaled, new Tui.Rect(0, 0, 64, 18));
            double ms = sw.Elapsed.TotalMilliseconds / 10;
            var (dec, w, h) = DecodeSixel(data);
            double psnr = Psnr(scaled.Pixels, scaled.Width, scaled.Height, dec, w, h);
            Console.WriteLine($"{name,-9}: {data.Length / 1024.0,7:F1} KB, {ms,5:F1} ms/klatkę, zdekodowano {w}x{h}, PSNR {psnr:F2} dB");
            SavePng(dec, w, h, Path.Combine(outDir, $"sixel_{name}.png"));
        }

        // Klatki wideo: przepustowość enkodera na prawdziwym materiale (dekoder Media Foundation — tylko Windows)
        if (!OperatingSystem.IsWindows()) return 0;
        Installer.Run();
        var info = await YtDlp.ResolveAsync(id, 480, false, CancellationToken.None);
        var vd = Mp4Demuxer.Open(info.VideoUrl!, "vide", CancellationToken.None);
        var t = vd.Track;
        var encV = new ImageEncoder { Mode = RenderMode.Sixel, AdaptivePalette = true, MaxColors = int.Parse(Environment.GetEnvironmentVariable("DIAG_COLORS") ?? "255") };
        var encH = new ImageEncoder { Mode = RenderMode.HalfBlock };
        var full = new uint[t.Width * t.Height];
        var frame = new uint[800 * 450];
        var small = new uint[160 * 90];
        long totalBytes = 0, totalHalf = 0;
        double totalMs = 0, convMs = 0;
        int n = 0;
        unsafe
        {
            using var dec = new H264Decoder(t.AvcConfig!, t.Width, t.Height);
            dec.OnFrame = (time, y, uv, stride) =>
            {
                if (n >= 60) return;
                var sw = Stopwatch.StartNew();
                ColorConvert.Nv12ToArgb(y, uv, stride, dec.CropX, dec.CropY, dec.DisplayWidth, dec.DisplayHeight, full, true);
                fixed (uint* f = full) Scaler.Resize(f, dec.DisplayWidth, dec.DisplayHeight, dec.DisplayWidth, frame, 800, 450);
                fixed (uint* f = full) Scaler.Resize(f, dec.DisplayWidth, dec.DisplayHeight, dec.DisplayWidth, small, 160, 90);
                convMs += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var b = encV.Encode(frame, 800, 450, new Tui.Rect(0, 0, 80, 23), true);
                totalMs += sw.Elapsed.TotalMilliseconds;
                totalBytes += b.Length;
                totalHalf += encH.Encode(small, 160, 90, new Tui.Rect(0, 0, 160, 45)).Length;
                if (n == 30)
                {
                    var (d, w, h) = DecodeSixel(b);
                    SavePng(d, w, h, Path.Combine(outDir, "sixel_video.png"));
                    Console.WriteLine($"   PSNR klatki wideo: {Psnr(frame, 800, 450, d, w, h):F2} dB");
                }
                n++;
            };
            foreach (var s in vd.ReadSamples(TimeSpan.FromSeconds(60), CancellationToken.None))
            {
                dec.Decode(new MediaSample(s.Buffer, s.Offset, s.Length, t.ToTime(s.Pts).Ticks, t.ToTime(s.Duration).Ticks, s.IsKey));
                if (n >= 60) break;
            }
        }
        Console.WriteLine($"wideo 800x450 Sixel: śr. {totalBytes / Math.Max(1, n) / 1024.0:F1} KB/klatkę, kodowanie {totalMs / Math.Max(1, n):F1} ms, konwersja+skalowanie {convMs / Math.Max(1, n):F1} ms");
        Console.WriteLine($"wideo 160x45 półbloki: śr. {totalHalf / Math.Max(1, n) / 1024.0:F1} KB/klatkę");
        return 0;
    }

    /// <summary>Minimalny dekoder Sixel (do weryfikacji enkodera).</summary>
    private static (uint[] Pixels, int W, int H) DecodeSixel(byte[] data)
    {
        int i = Array.IndexOf(data, (byte)'q');
        var pal = new uint[256];
        int w = 0, h = 0;
        uint[] px = [];
        int x = 0, y = 0, color = 0;
        int ReadNum(ref int p)
        {
            int v = 0;
            while (p < data.Length && data[p] >= '0' && data[p] <= '9') v = v * 10 + (data[p++] - '0');
            return v;
        }
        int p = i + 1;
        while (p < data.Length)
        {
            byte c = data[p];
            if (c == 0x1b) break;
            if (c == '"')
            {
                p++; ReadNum(ref p); p++; ReadNum(ref p); p++;
                w = ReadNum(ref p); p++; h = ReadNum(ref p);
                px = new uint[w * h];
                continue;
            }
            if (c == '#')
            {
                p++;
                int reg = ReadNum(ref p);
                if (p < data.Length && data[p] == ';')
                {
                    p++; ReadNum(ref p); p++;
                    int r = ReadNum(ref p); p++;
                    int g = ReadNum(ref p); p++;
                    int b = ReadNum(ref p);
                    pal[reg] = 0xFF000000u | ((uint)(r * 255 / 100) << 16) | ((uint)(g * 255 / 100) << 8) | (uint)(b * 255 / 100);
                }
                color = reg;
                continue;
            }
            if (c == '$') { x = 0; p++; continue; }
            if (c == '-') { x = 0; y += 6; p++; continue; }
            int rep = 1;
            if (c == '!') { p++; rep = ReadNum(ref p); c = data[p]; }
            if (c >= 63 && c <= 126)
            {
                int bits = c - 63;
                for (int k = 0; k < rep; k++, x++)
                    for (int b = 0; b < 6; b++)
                        if ((bits & (1 << b)) != 0 && x < w && y + b < h) px[(y + b) * w + x] = pal[color];
            }
            p++;
        }
        return (px, w, h);
    }

    private static double Psnr(uint[] a, int aw, int ah, uint[] b, int bw, int bh)
    {
        int w = Math.Min(aw, bw), h = Math.Min(ah, bh);
        double se = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = a[y * aw + x], q = b[y * bw + x];
                for (int s = 0; s < 24; s += 8)
                {
                    double d = (int)((p >> s) & 0xFF) - (int)((q >> s) & 0xFF);
                    se += d * d;
                }
            }
        double mse = se / (w * h * 3.0);
        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static void SavePng(uint[] px, int w, int h, string path)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                uint c = px[y * w + x];
                raw.WriteByte((byte)(c >> 16)); raw.WriteByte((byte)(c >> 8)); raw.WriteByte((byte)c);
            }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true))
            raw.WriteTo(zs);
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            fs.Write(len);
            var tb = System.Text.Encoding.ASCII.GetBytes(type);
            fs.Write(tb);
            fs.Write(body);
            uint crc = Crc32(tb, 0xFFFFFFFF);
            crc = Crc32(body, crc) ^ 0xFFFFFFFF;
            var cb = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cb, crc);
            fs.Write(cb);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        Console.WriteLine("   zapisano " + path);
    }

    private static uint[]? _crcTable;
    private static uint Crc32(byte[] data, uint crc)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                _crcTable[n] = c;
            }
        }
        foreach (var b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void SavePpm(uint[] px, int w, int h, string path)
    {
        using var fs = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header);
        foreach (var p in px) { fs.WriteByte((byte)(p >> 16)); fs.WriteByte((byte)(p >> 8)); fs.WriteByte((byte)p); }
        Console.WriteLine("   zapisano " + path);
    }
}
