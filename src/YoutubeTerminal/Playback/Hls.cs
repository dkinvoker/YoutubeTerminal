using System.Buffers.Binary;
using System.Text;

namespace YoutubeTerminal.Playback;

/// <summary>
/// Wspólna oś czasu dla ścieżek transmisji na żywo: znaczniki PTS (90 kHz) obu ścieżek
/// są przeliczane względem tej samej bazy.
/// </summary>
internal sealed class HlsShared
{
    private long _base = long.MinValue;
    private long _startSeq = -1;
    private readonly object _lock = new();

    /// <summary>Wspólny numer segmentu startowego — ustala go ścieżka, która pierwsza wczyta listę.</summary>
    public long GetStart(Func<long> choose)
    {
        lock (_lock)
        {
            if (_startSeq < 0) _startSeq = choose();
            return _startSeq;
        }
    }

    public long ToTicks(long pts90k)
    {
        if (Interlocked.Read(ref _base) == long.MinValue)
            Interlocked.CompareExchange(ref _base, pts90k - 90_000L * 30, long.MinValue); // 30 s zapasu
        long d = pts90k - Interlocked.Read(ref _base);
        return d * 1000 / 9; // 90 kHz -> 100 ns
    }
}

/// <summary>Lista mediów HLS (odświeżana dla transmisji na żywo).</summary>
internal sealed class HlsPlaylist
{
    public long MediaSequence;
    public double TargetDuration = 5;
    public bool Ended;
    public List<(long Seq, string Url, double Duration)> Segments = new();

    public static async Task<HlsPlaylist> LoadAsync(string url, CancellationToken ct)
    {
        var text = await HttpRangeReader.Shared.GetStringAsync(url, ct).ConfigureAwait(false);
        var pl = new HlsPlaylist();
        double dur = 0;
        long seq = -1;
        var baseUri = new Uri(url);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line[22..], out pl.MediaSequence);
                seq = pl.MediaSequence;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                double.TryParse(line[22..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pl.TargetDuration);
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var v = line[8..].Split(',')[0];
                double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dur);
            }
            else if (line == "#EXT-X-ENDLIST") pl.Ended = true;
            else if (!line.StartsWith('#'))
            {
                if (seq < 0) seq = 0;
                pl.Segments.Add((seq, new Uri(baseUri, line).ToString(), dur));
                seq++;
            }
        }
        return pl;
    }
}

/// <summary>Bazowa logika: pobieranie kolejnych segmentów od krawędzi transmisji, z odświeżaniem listy.</summary>
internal abstract class HlsSourceBase : IMediaSource
{
    protected readonly string PlaylistUrl;
    protected readonly HlsShared Timeline;

    public abstract string Codec { get; }
    public virtual byte[]? AvcConfig => null;
    public byte[]? AudioConfig { get; protected set; }
    public abstract bool AnnexB { get; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int SampleRate { get; protected set; }
    public int Channels { get; protected set; }
    public TimeSpan Duration => TimeSpan.Zero;
    public bool IsLive => true;
    /// <summary>Numer pierwszego segmentu (wspólny dla obrazu i dźwięku, by zacząć w tym samym miejscu).</summary>
    public long StartSequence { get; private set; } = -1;

    protected HlsSourceBase(string playlistUrl, HlsShared timeline)
    {
        PlaylistUrl = playlistUrl;
        Timeline = timeline;
    }

    /// <summary>Wybiera segment startowy: kilka segmentów przed krawędzią transmisji.</summary>
    public static long ChooseStart(HlsPlaylist pl, int segmentsBehind = 3)
    {
        if (pl.Segments.Count == 0) return pl.MediaSequence;
        return pl.Segments[Math.Max(0, pl.Segments.Count - segmentsBehind)].Seq;
    }

    public IEnumerable<MediaSample> Read(TimeSpan start, CancellationToken ct)
    {
        long next = -1;
        Task<byte[]>? prefetch = null;
        long prefetchSeq = -1;
        var pending = new Queue<(long Seq, string Url)>();
        DateTime lastRefresh = DateTime.MinValue;
        int emptyRefreshes = 0;

        while (!ct.IsCancellationRequested)
        {
            if (pending.Count == 0)
            {
                // Odświeżenie listy (nie częściej niż co pół długości segmentu).
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - lastRefresh);
                if (next >= 0 && wait > TimeSpan.Zero) Thread.Sleep(wait);
                var pl = HlsPlaylist.LoadAsync(PlaylistUrl, ct).GetAwaiter().GetResult();
                lastRefresh = DateTime.UtcNow;
                if (next < 0)
                {
                    var plCopy = pl;
                    next = Timeline.GetStart(() => ChooseStart(plCopy));
                    StartSequence = next;
                }
                foreach (var s in pl.Segments)
                    if (s.Seq >= next) { pending.Enqueue((s.Seq, s.Url)); next = s.Seq + 1; }
                if (pending.Count == 0)
                {
                    if (pl.Ended) yield break;
                    if (++emptyRefreshes > 30) throw new IOException("Transmisja nie dostarcza nowych segmentów.");
                    continue;
                }
                emptyRefreshes = 0;
            }

            var (seq, url) = pending.Dequeue();
            byte[] data;
            if (prefetch != null && prefetchSeq == seq) data = prefetch.GetAwaiter().GetResult();
            else data = HttpRangeReader.Shared.GetByteArrayAsync(url, ct).GetAwaiter().GetResult();
            prefetch = null;
            if (pending.Count > 0)
            {
                var (ns, nu) = pending.Peek();
                prefetchSeq = ns;
                prefetch = HttpRangeReader.Shared.GetByteArrayAsync(nu, ct);
            }

            foreach (var sample in ParseSegment(data))
                yield return sample;
        }
    }

    protected abstract IEnumerable<MediaSample> ParseSegment(byte[] data);

    // ----------- MPEG-TS -----------

    /// <summary>Demultipleksuje pakiety PES danego typu strumienia z segmentu MPEG-TS.</summary>
    protected static List<(byte[] Payload, long Pts, long Dts)> DemuxTs(byte[] data, Func<int, bool> wantStreamType)
    {
        var result = new List<(byte[], long, long)>();
        int pmtPid = -1;
        var wanted = new HashSet<int>();
        var buffers = new Dictionary<int, MemoryStream>();

        void Flush(int pid)
        {
            if (!buffers.TryGetValue(pid, out var ms) || ms.Length < 9) return;
            var pes = ms.ToArray();
            ms.SetLength(0);
            if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1) return;
            int flags = pes[7];
            int hdrLen = pes[8];
            long pts = -1, dts = -1;
            if ((flags & 0x80) != 0 && pes.Length >= 14) pts = ReadPts(pes, 9);
            if ((flags & 0x40) != 0 && pes.Length >= 19) dts = ReadPts(pes, 14);
            if (dts < 0) dts = pts;
            int start = 9 + hdrLen;
            if (start >= pes.Length) return;
            result.Add((pes.AsSpan(start).ToArray(), pts, dts));
        }

        for (int p = 0; p + 188 <= data.Length; p += 188)
        {
            if (data[p] != 0x47)
            {
                // Utrata synchronizacji — szukamy następnego bajtu 0x47.
                int k = Array.IndexOf(data, (byte)0x47, p + 1);
                if (k < 0) break;
                p = k - 188;
                continue;
            }
            bool pusi = (data[p + 1] & 0x40) != 0;
            int pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
            int afc = (data[p + 3] >> 4) & 3;
            int off = p + 4;
            if (afc == 2) continue;
            if (afc == 3) off += 1 + data[p + 4];
            if (off >= p + 188) continue;
            var payload = data.AsSpan(off, p + 188 - off);

            if (pid == 0 && pusi)
            {
                int ptr = payload[0];
                var sec = payload[(1 + ptr)..];
                if (sec.Length >= 12) pmtPid = ((sec[10] & 0x1F) << 8) | sec[11];
            }
            else if (pid == pmtPid && pusi)
            {
                int ptr = payload[0];
                var sec = payload[(1 + ptr)..];
                if (sec.Length < 12) continue;
                int secLen = ((sec[1] & 0x0F) << 8) | sec[2];
                int progInfoLen = ((sec[10] & 0x0F) << 8) | sec[11];
                int q = 12 + progInfoLen;
                int end = Math.Min(sec.Length, 3 + secLen - 4);
                while (q + 5 <= end)
                {
                    int type = sec[q];
                    int epid = ((sec[q + 1] & 0x1F) << 8) | sec[q + 2];
                    int esInfo = ((sec[q + 3] & 0x0F) << 8) | sec[q + 4];
                    if (wantStreamType(type)) wanted.Add(epid);
                    q += 5 + esInfo;
                }
            }
            else if (wanted.Contains(pid))
            {
                if (pusi) Flush(pid);
                if (!buffers.TryGetValue(pid, out var ms)) buffers[pid] = ms = new MemoryStream();
                ms.Write(payload);
            }
        }
        foreach (var pid in buffers.Keys.ToList()) Flush(pid);
        return result;
    }

    private static long ReadPts(byte[] b, int o) =>
        ((long)(b[o] & 0x0E) << 29) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] & 0xFE) << 14) | ((long)b[o + 3] << 7) | ((long)b[o + 4] >> 1);
}

/// <summary>Wideo H.264 z segmentów MPEG-TS (Annex B z SPS/PPS w strumieniu).</summary>
internal sealed class HlsVideoSource : HlsSourceBase
{
    public HlsVideoSource(string url, HlsShared timeline, int width, int height) : base(url, timeline)
    {
        Width = width;
        Height = height;
    }

    public override string Codec => "avc1";
    public override bool AnnexB => true;

    protected override IEnumerable<MediaSample> ParseSegment(byte[] data)
    {
        var pes = DemuxTs(data, t => t == 0x1B);
        for (int i = 0; i < pes.Count; i++)
        {
            var (payload, pts, dts) = pes[i];
            if (pts < 0) continue;
            long dur = i + 1 < pes.Count && pes[i + 1].Dts > dts ? (pes[i + 1].Dts - dts) * 1000 / 9 : 333_333;
            yield return new MediaSample(payload, 0, payload.Length, Timeline.ToTicks(pts), dur, ContainsIdr(payload));
        }
    }

    private static bool ContainsIdr(byte[] b)
    {
        for (int i = 0; i + 3 < b.Length; i++)
        {
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
            {
                int type = b[i + 3] & 0x1F;
                if (type == 5) return true;
                i += 2;
            }
        }
        return false;
    }
}

/// <summary>Audio AAC: „packed audio” (ID3 + ADTS) albo AAC w MPEG-TS.</summary>
internal sealed class HlsAudioSource : HlsSourceBase
{
    public HlsAudioSource(string url, HlsShared timeline, byte[]? asc = null, int rate = 0, int channels = 0) : base(url, timeline)
    {
        AudioConfig = asc;
        SampleRate = rate;
        Channels = channels;
    }

    public override string Codec => "mp4a";
    public override bool AnnexB => false;

    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    protected override IEnumerable<MediaSample> ParseSegment(byte[] data)
    {
        if (data.Length > 0 && data[0] == 0x47)
        {
            foreach (var (payload, pts, _) in DemuxTs(data, t => t == 0x0F))
                foreach (var s in ParseAdts(payload, 0, pts)) yield return s;
            yield break;
        }

        int p = 0;
        long pts90k = -1;
        // Znaczniki ID3 (mogą występować kilka razy) — PRIV com.apple.streaming.transportStreamTimestamp
        while (p + 10 <= data.Length && data[p] == 'I' && data[p + 1] == 'D' && data[p + 2] == '3')
        {
            int size = (data[p + 6] << 21) | (data[p + 7] << 14) | (data[p + 8] << 7) | data[p + 9];
            bool v24 = data[p + 3] >= 4;
            int end = Math.Min(data.Length, p + 10 + size);
            int q = p + 10;
            while (q + 10 <= end)
            {
                string id = Encoding.ASCII.GetString(data, q, 4);
                int fsize = v24
                    ? (data[q + 4] << 21) | (data[q + 5] << 14) | (data[q + 6] << 7) | data[q + 7]
                    : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(q + 4));
                if (fsize <= 0 || q + 10 + fsize > end) break;
                if (id == "PRIV")
                {
                    int z = Array.IndexOf(data, (byte)0, q + 10, fsize);
                    if (z > 0 && Encoding.ASCII.GetString(data, q + 10, z - q - 10) == "com.apple.streaming.transportStreamTimestamp" && z + 9 <= end)
                        pts90k = (long)(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(z + 1)) & 0x1FFFFFFFFL);
                }
                q += 10 + fsize;
            }
            p = end;
        }
        foreach (var s in ParseAdts(data, p, pts90k < 0 ? 0 : pts90k)) yield return s;
    }

    private IEnumerable<MediaSample> ParseAdts(byte[] data, int p, long pts90k)
    {
        long frame = 0;
        while (p + 7 <= data.Length)
        {
            if (data[p] != 0xFF || (data[p + 1] & 0xF0) != 0xF0) { p++; continue; }
            bool noCrc = (data[p + 1] & 1) != 0;
            int profile = (data[p + 2] >> 6) + 1;
            int freqIdx = (data[p + 2] >> 2) & 0xF;
            int ch = ((data[p + 2] & 1) << 2) | (data[p + 3] >> 6);
            int len = ((data[p + 3] & 3) << 11) | (data[p + 4] << 3) | (data[p + 5] >> 5);
            int hdr = noCrc ? 7 : 9;
            if (len < hdr || p + len > data.Length) break;
            if (AudioConfig == null && freqIdx < Rates.Length)
            {
                SampleRate = Rates[freqIdx];
                Channels = ch;
                int v = (profile << 11) | (freqIdx << 7) | (ch << 3);
                AudioConfig = [(byte)(v >> 8), (byte)v];
            }
            int rate = SampleRate > 0 ? SampleRate : 44100;
            long t = pts90k + frame * 1024 * 90000 / rate;
            long dur = 1024L * 10_000_000 / rate;
            yield return new MediaSample(data, p + hdr, len - hdr, Timeline.ToTicks(t), dur, true);
            frame++;
            p += len;
        }
    }

    /// <summary>Wczytuje pierwszy segment, by poznać parametry AAC (potrzebne do utworzenia dekodera).</summary>
    public void Probe(CancellationToken ct)
    {
        var pl = HlsPlaylist.LoadAsync(PlaylistUrl, ct).GetAwaiter().GetResult();
        if (pl.Segments.Count == 0) return;
        var data = HttpRangeReader.Shared.GetByteArrayAsync(pl.Segments[^1].Url, ct).GetAwaiter().GetResult();
        var probeTimeline = new HlsShared();
        var probe = new HlsAudioSource(PlaylistUrl, probeTimeline);
        foreach (var _ in probe.ParseSegment(data)) break;
        AudioConfig = probe.AudioConfig;
        SampleRate = probe.SampleRate;
        Channels = probe.Channels;
    }
}
