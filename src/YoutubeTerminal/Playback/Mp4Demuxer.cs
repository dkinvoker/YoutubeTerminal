using System.Buffers.Binary;
using System.Text;

namespace YoutubeTerminal.Playback;

/// <summary>Pojedyncza próbka (ramka) ze ścieżki MP4.</summary>
internal readonly struct Mp4Sample
{
    public readonly byte[] Buffer;
    public readonly int Offset;
    public readonly int Length;
    public readonly long Dts;
    public readonly long Pts;
    public readonly long Duration;
    public readonly bool IsKey;

    public Mp4Sample(byte[] buffer, int offset, int length, long dts, long pts, long duration, bool isKey)
    {
        Buffer = buffer; Offset = offset; Length = length; Dts = dts; Pts = pts; Duration = duration; IsKey = isKey;
    }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(Offset, Length);
}

internal sealed class Mp4Track
{
    public uint Id;
    public uint Timescale = 1000;
    public string Handler = "";
    public string Codec = "";
    public byte[]? AvcConfig;
    public byte[]? AudioConfig;
    public int Width, Height, Channels, SampleRate;
    public long Duration;
    public uint DefaultDuration, DefaultSize, DefaultFlags;

    // Tablice próbek (tylko dla plików nie-fragmentowanych)
    public long[]? Offsets;
    public int[]? Sizes;
    public long[]? Dts;
    public int[]? Cto;
    public bool[]? Sync;

    public TimeSpan ToTime(long t) => TimeSpan.FromTicks((long)((double)t * TimeSpan.TicksPerSecond / Timescale));
    public long FromTime(TimeSpan t) => (long)(t.Ticks / (double)TimeSpan.TicksPerSecond * Timescale);
}

/// <summary>
/// Demultiplekser MP4 obsługujący zarówno pliki fragmentowane (DASH: sidx/moof/mdat),
/// jak i klasyczne (moov z tablicami próbek). Dane pobiera przez <see cref="HttpRangeReader"/>.
/// </summary>
internal sealed class Mp4Demuxer
{
    private readonly HttpRangeReader _http;
    private byte[] _head = [];
    private readonly List<Mp4Track> _tracks = new();
    private List<(long Offset, long Size, long Start, long Duration)>? _segments;
    private uint _sidxTimescale;
    private long _firstFragmentOffset = -1;

    public Mp4Track Track { get; private set; } = null!;
    public IReadOnlyList<Mp4Track> Tracks => _tracks;
    public bool IsFragmented => _firstFragmentOffset >= 0;
    public TimeSpan Duration { get; private set; }

    private Mp4Demuxer(HttpRangeReader http) => _http = http;

    public static Mp4Demuxer Open(string url, string handler, CancellationToken ct)
    {
        var d = new Mp4Demuxer(new HttpRangeReader(url));
        d.ParseHeader(ct);
        d.Track = d._tracks.FirstOrDefault(t => t.Handler == handler)
                  ?? throw new InvalidDataException(handler == "vide" ? L.T("Brak ścieżki wideo w strumieniu.", "No video track in the stream.") : L.T("Brak ścieżki audio w strumieniu.", "No audio track in the stream."));
        if (d.Track.Duration > 0) d.Duration = d.Track.ToTime(d.Track.Duration);
        if (d._segments is { Count: > 0 } segs && d._sidxTimescale > 0)
        {
            var last = segs[^1];
            d.Duration = TimeSpan.FromSeconds((last.Start + last.Duration) / (double)d._sidxTimescale);
        }
        return d;
    }

    // ---------------- Nagłówek ----------------

    private byte[] ReadAt(long offset, int count, CancellationToken ct)
    {
        if (offset + count <= _head.Length)
            return _head.AsSpan((int)offset, count).ToArray();
        return _http.Read(offset, count, ct);
    }

    private void ParseHeader(CancellationToken ct)
    {
        _head = _http.Read(0, 256 * 1024, ct);
        if (_head.Length < 16) throw new InvalidDataException(L.T("Strumień jest pusty.", "The stream is empty."));

        long pos = 0;
        bool haveMoov = false;
        long fileLen = _http.Length > 0 ? _http.Length : long.MaxValue;
        for (int guard = 0; guard < 64 && pos < fileLen; guard++)
        {
            var hdr = ReadAt(pos, 16, ct);
            if (hdr.Length < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            int hdrLen = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); hdrLen = 16; }
            else if (size == 0) size = (fileLen == long.MaxValue ? 0 : fileLen - pos);
            if (size < hdrLen) break;

            switch (type)
            {
                case "moov":
                    var moov = ReadAt(pos, (int)size, ct);
                    ParseMoov(moov.AsSpan(hdrLen));
                    haveMoov = true;
                    break;
                case "sidx":
                    var sidx = ReadAt(pos, (int)size, ct);
                    ParseSidx(sidx.AsSpan(hdrLen), pos + size);
                    break;
                case "moof":
                    if (_firstFragmentOffset < 0) _firstFragmentOffset = pos;
                    break;
            }

            if (haveMoov && (type == "moof" || type == "mdat" && _firstFragmentOffset < 0 && _tracks.Any(t => t.Offsets != null)))
                break;
            if (haveMoov && _segments != null) { if (_firstFragmentOffset < 0) _firstFragmentOffset = _segments[0].Offset; break; }
            pos += size;
        }

        if (!haveMoov) throw new InvalidDataException(L.T("Nie znaleziono nagłówka moov w pliku MP4.", "No moov header found in the MP4 file."));
    }

    private void ParseSidx(ReadOnlySpan<byte> b, long boxEnd)
    {
        int version = b[0];
        int p = 4;
        p += 4; // reference_ID
        _sidxTimescale = BE32(b, p); p += 4;
        long earliest, firstOffset;
        if (version == 0) { earliest = BE32(b, p); firstOffset = BE32(b, p + 4); p += 8; }
        else { earliest = (long)BE64(b, p); firstOffset = (long)BE64(b, p + 8); p += 16; }
        p += 2;
        int count = BinaryPrimitives.ReadUInt16BigEndian(b[p..]); p += 2;
        var segs = new List<(long, long, long, long)>(count);
        long offset = boxEnd + firstOffset;
        long time = earliest;
        for (int i = 0; i < count && p + 12 <= b.Length; i++)
        {
            uint refSize = BE32(b, p) & 0x7FFFFFFF;
            uint dur = BE32(b, p + 4);
            p += 12;
            segs.Add((offset, refSize, time, dur));
            offset += refSize;
            time += dur;
        }
        if (segs.Count > 0) _segments = segs;
    }

    private void ParseMoov(ReadOnlySpan<byte> b)
    {
        var trexDefaults = new Dictionary<uint, (uint dur, uint size, uint flags)>();
        foreach (var (type, body) in Boxes(b))
        {
            if (type == "trak") ParseTrak(body);
            else if (type == "mvex")
            {
                foreach (var (t2, b2) in Boxes(body))
                    if (t2 == "trex" && b2.Length >= 24)
                        trexDefaults[BE32(b2, 4)] = (BE32(b2, 12), BE32(b2, 16), BE32(b2, 20));
            }
        }
        foreach (var t in _tracks)
            if (trexDefaults.TryGetValue(t.Id, out var d))
                (t.DefaultDuration, t.DefaultSize, t.DefaultFlags) = d;
    }

    private void ParseTrak(ReadOnlySpan<byte> b)
    {
        var t = new Mp4Track();
        foreach (var (type, body) in Boxes(b))
        {
            if (type == "tkhd")
                t.Id = body[0] == 1 ? BE32(body, 20) : BE32(body, 12);
            else if (type == "mdia")
            {
                foreach (var (t2, b2) in Boxes(body))
                {
                    if (t2 == "mdhd")
                    {
                        if (b2[0] == 1) { t.Timescale = BE32(b2, 20); t.Duration = (long)BE64(b2, 24); }
                        else { t.Timescale = BE32(b2, 12); t.Duration = BE32(b2, 16); }
                    }
                    else if (t2 == "hdlr" && b2.Length >= 12)
                        t.Handler = Encoding.ASCII.GetString(b2.Slice(8, 4));
                    else if (t2 == "minf")
                    {
                        foreach (var (t3, b3) in Boxes(b2))
                            if (t3 == "stbl") ParseStbl(b3, t);
                    }
                }
            }
        }
        if (t.Timescale == 0) t.Timescale = 1000;
        _tracks.Add(t);
    }

    private static void ParseStbl(ReadOnlySpan<byte> b, Mp4Track t)
    {
        uint[]? sttsCount = null, sttsDelta = null, cttsCount = null;
        int[]? cttsOff = null;
        uint[]? stss = null;
        (uint first, uint per)[]? stsc = null;
        long[]? chunkOffsets = null;
        int[]? sizes = null;
        uint fixedSize = 0, sampleCount = 0;

        foreach (var (type, body) in Boxes(b))
        {
            switch (type)
            {
                case "stsd":
                    ParseStsd(body, t);
                    break;
                case "stts":
                {
                    uint n = BE32(body, 4);
                    sttsCount = new uint[n]; sttsDelta = new uint[n];
                    for (int i = 0; i < n; i++) { sttsCount[i] = BE32(body, 8 + i * 8); sttsDelta[i] = BE32(body, 12 + i * 8); }
                    break;
                }
                case "ctts":
                {
                    uint n = BE32(body, 4);
                    cttsCount = new uint[n]; cttsOff = new int[n];
                    for (int i = 0; i < n; i++) { cttsCount[i] = BE32(body, 8 + i * 8); cttsOff[i] = (int)BE32(body, 12 + i * 8); }
                    break;
                }
                case "stss":
                {
                    uint n = BE32(body, 4);
                    stss = new uint[n];
                    for (int i = 0; i < n; i++) stss[i] = BE32(body, 8 + i * 4);
                    break;
                }
                case "stsc":
                {
                    uint n = BE32(body, 4);
                    stsc = new (uint, uint)[n];
                    for (int i = 0; i < n; i++) stsc[i] = (BE32(body, 8 + i * 12), BE32(body, 12 + i * 12));
                    break;
                }
                case "stsz":
                {
                    fixedSize = BE32(body, 4);
                    sampleCount = BE32(body, 8);
                    if (fixedSize == 0)
                    {
                        sizes = new int[sampleCount];
                        for (int i = 0; i < sampleCount; i++) sizes[i] = (int)BE32(body, 12 + i * 4);
                    }
                    break;
                }
                case "stco":
                {
                    uint n = BE32(body, 4);
                    chunkOffsets = new long[n];
                    for (int i = 0; i < n; i++) chunkOffsets[i] = BE32(body, 8 + i * 4);
                    break;
                }
                case "co64":
                {
                    uint n = BE32(body, 4);
                    chunkOffsets = new long[n];
                    for (int i = 0; i < n; i++) chunkOffsets[i] = (long)BE64(body, 8 + i * 8);
                    break;
                }
            }
        }

        if (sampleCount == 0 || chunkOffsets == null || stsc == null || sttsCount == null) return;

        int count = (int)sampleCount;
        sizes ??= Enumerable.Repeat((int)fixedSize, count).ToArray();
        var offsets = new long[count];
        int s = 0;
        for (int e = 0; e < stsc.Length && s < count; e++)
        {
            long firstChunk = stsc[e].first - 1;
            long lastChunk = e + 1 < stsc.Length ? stsc[e + 1].first - 2 : chunkOffsets.Length - 1;
            for (long c = firstChunk; c <= lastChunk && c < chunkOffsets.Length && s < count; c++)
            {
                long off = chunkOffsets[c];
                for (int k = 0; k < stsc[e].per && s < count; k++)
                {
                    offsets[s] = off;
                    off += sizes[s];
                    s++;
                }
            }
        }

        var dts = new long[count];
        long cur = 0;
        int idx = 0;
        for (int e = 0; e < sttsCount.Length && idx < count; e++)
            for (uint k = 0; k < sttsCount[e] && idx < count; k++) { dts[idx++] = cur; cur += sttsDelta![e]; }
        for (; idx < count; idx++) dts[idx] = cur;

        int[]? cto = null;
        if (cttsCount != null)
        {
            cto = new int[count];
            idx = 0;
            for (int e = 0; e < cttsCount.Length && idx < count; e++)
                for (uint k = 0; k < cttsCount[e] && idx < count; k++) cto[idx++] = cttsOff![e];
        }

        bool[]? sync = null;
        if (stss != null)
        {
            sync = new bool[count];
            foreach (var n in stss) if (n >= 1 && n <= count) sync[n - 1] = true;
        }

        t.Offsets = offsets; t.Sizes = sizes; t.Dts = dts; t.Cto = cto; t.Sync = sync;
    }

    private static void ParseStsd(ReadOnlySpan<byte> b, Mp4Track t)
    {
        // pełny box: version/flags (4) + entry_count (4), potem pierwsza pozycja
        var entries = b[8..];
        foreach (var (type, body) in Boxes(entries))
        {
            t.Codec = type;
            if (type is "avc1" or "avc3")
            {
                t.Width = BinaryPrimitives.ReadUInt16BigEndian(body[24..]);
                t.Height = BinaryPrimitives.ReadUInt16BigEndian(body[26..]);
                foreach (var (t2, b2) in Boxes(body[78..]))
                    if (t2 == "avcC") t.AvcConfig = b2.ToArray();
            }
            else if (type is "mp4a")
            {
                t.Channels = BinaryPrimitives.ReadUInt16BigEndian(body[16..]);
                t.SampleRate = (int)(BE32(body, 24) >> 16);
                foreach (var (t2, b2) in Boxes(body[28..]))
                    if (t2 == "esds") t.AudioConfig = ParseEsds(b2);
            }
            else if (body.Length >= 28 && t.Width == 0)
            {
                t.Width = BinaryPrimitives.ReadUInt16BigEndian(body[24..]);
                t.Height = BinaryPrimitives.ReadUInt16BigEndian(body[26..]);
            }
            break; // tylko pierwsza pozycja
        }
    }

    private static byte[]? ParseEsds(ReadOnlySpan<byte> b)
    {
        int p = 4; // version/flags
        while (p < b.Length)
        {
            int tag = b[p++];
            int len = 0;
            for (int i = 0; i < 4 && p < b.Length; i++)
            {
                int c = b[p++];
                len = (len << 7) | (c & 0x7F);
                if ((c & 0x80) == 0) break;
            }
            switch (tag)
            {
                case 0x03: // ES_Descriptor
                {
                    p += 2;
                    int flags = b[p++];
                    if ((flags & 0x80) != 0) p += 2;
                    if ((flags & 0x40) != 0) p += 1 + b[p];
                    if ((flags & 0x20) != 0) p += 2;
                    continue;
                }
                case 0x04: // DecoderConfigDescriptor
                    p += 13;
                    continue;
                case 0x05: // DecoderSpecificInfo = AudioSpecificConfig
                    return b.Slice(p, Math.Min(len, b.Length - p)).ToArray();
                default:
                    p += len;
                    break;
            }
        }
        return null;
    }

    // ---------------- Próbki ----------------

    /// <summary>Zwraca próbki od miejsca najbliższego (nie późniejszego) punktu dostępu względem <paramref name="start"/>.</summary>
    public IEnumerable<Mp4Sample> ReadSamples(TimeSpan start, CancellationToken ct)
    {
        if (Track.Offsets != null && !IsFragmented)
            return ReadProgressive(start, ct);
        if (_segments != null)
            return ReadSegments(start, ct);
        return ReadFragmentsSequential(start, ct);
    }

    private IEnumerable<Mp4Sample> ReadSegments(TimeSpan start, CancellationToken ct)
    {
        var segs = _segments!;
        long target = (long)(start.TotalSeconds * _sidxTimescale);
        int i = 0;
        for (int k = 0; k < segs.Count; k++)
        {
            if (segs[k].Start <= target) i = k; else break;
        }

        Task<byte[]>? next = null;
        for (; i < segs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var seg = segs[i];
            byte[] data = next != null ? next.GetAwaiter().GetResult() : _http.Read(seg.Offset, (int)seg.Size, ct);
            next = null;
            if (i + 1 < segs.Count)
            {
                var ns = segs[i + 1];
                next = HttpRangeReader.Observed(Task.Run(() => _http.Read(ns.Offset, (int)ns.Size, ct), ct));
            }
            foreach (var s in ParseFragments(data, seg.Offset))
                yield return s;
        }
    }

    private IEnumerable<Mp4Sample> ReadFragmentsSequential(TimeSpan start, CancellationToken ct)
    {
        long pos = _firstFragmentOffset;
        long target = Track.FromTime(start);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var hdr = _http.Read(pos, 16, ct);
            if (hdr.Length < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
            if (size < 8) yield break;
            if (type != "moof") { pos += size; continue; }

            var moof = _http.Read(pos, (int)size, ct);
            var mdatHdr = _http.Read(pos + size, 16, ct);
            long mdatSize = mdatHdr.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(mdatHdr) : 0;
            if (mdatSize == 1 && mdatHdr.Length >= 16) mdatSize = (long)BinaryPrimitives.ReadUInt64BigEndian(mdatHdr.AsSpan(8));

            // Szybkie pominięcie fragmentów przed punktem startu (bez pobierania mdat).
            var probe = ParseFragments(moof, pos, dataAvailable: false).ToList();
            bool before = probe.Count > 0 && probe[^1].Dts + probe[^1].Duration < target;
            if (!before)
            {
                var full = _http.Read(pos, (int)(size + mdatSize), ct);
                foreach (var s in ParseFragments(full, pos)) yield return s;
            }
            pos += size + mdatSize;
        }
    }

    private IEnumerable<Mp4Sample> ParseFragments(byte[] data, long absBase, bool dataAvailable = true)
    {
        var result = new List<Mp4Sample>();
        int p = 0;
        while (p + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            int hl = 8;
            if (size == 1 && p + 16 <= data.Length) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8)); hl = 16; }
            if (size == 0) size = data.Length - p;
            if (size < hl) break;
            if (type == "moof")
            {
                int end = (int)Math.Min(data.Length, p + size);
                ParseMoof(data, p, hl, end, absBase, result, dataAvailable);
            }
            p += (int)Math.Min(size, int.MaxValue);
        }
        return result;
    }

    private void ParseMoof(byte[] data, int moofPos, int hl, int end, long absBase, List<Mp4Sample> output, bool dataAvailable)
    {
        long moofAbs = absBase + moofPos;
        var moof = data.AsSpan(moofPos + hl, end - moofPos - hl);
        foreach (var (type, traf) in Boxes(moof))
        {
            if (type != "traf") continue;

            uint trackId = 0, defDur = Track.DefaultDuration, defSize = Track.DefaultSize, defFlags = Track.DefaultFlags;
            long baseOffset = moofAbs;
            long decodeTime = 0;
            bool skip = false;
            long nextDataAbs = -1;

            foreach (var (t2, b2) in Boxes(traf))
            {
                if (t2 == "tfhd")
                {
                    uint flags = BE32(b2, 0) & 0xFFFFFF;
                    trackId = BE32(b2, 4);
                    int q = 8;
                    if ((flags & 0x01) != 0) { baseOffset = (long)BE64(b2, q); q += 8; }
                    if ((flags & 0x02) != 0) q += 4;
                    if ((flags & 0x08) != 0) { defDur = BE32(b2, q); q += 4; }
                    if ((flags & 0x10) != 0) { defSize = BE32(b2, q); q += 4; }
                    if ((flags & 0x20) != 0) { defFlags = BE32(b2, q); q += 4; }
                    skip = trackId != Track.Id && _tracks.Count > 1;
                }
                else if (t2 == "tfdt")
                {
                    decodeTime = b2[0] == 1 ? (long)BE64(b2, 4) : BE32(b2, 4);
                }
                else if (t2 == "trun" && !skip)
                {
                    int version = b2[0];
                    uint flags = BE32(b2, 0) & 0xFFFFFF;
                    uint count = BE32(b2, 4);
                    int q = 8;
                    long dataAbs = nextDataAbs >= 0 ? nextDataAbs : baseOffset;
                    if ((flags & 0x01) != 0) { dataAbs = baseOffset + (int)BE32(b2, q); q += 4; }
                    uint firstFlags = defFlags;
                    bool hasFirst = (flags & 0x04) != 0;
                    if (hasFirst) { firstFlags = BE32(b2, q); q += 4; }

                    for (uint i = 0; i < count; i++)
                    {
                        uint dur = defDur, size = defSize, sflags = defFlags;
                        int cto = 0;
                        if ((flags & 0x100) != 0) { dur = BE32(b2, q); q += 4; }
                        if ((flags & 0x200) != 0) { size = BE32(b2, q); q += 4; }
                        if ((flags & 0x400) != 0) { sflags = BE32(b2, q); q += 4; }
                        else if (i == 0 && hasFirst) sflags = firstFlags;
                        if ((flags & 0x800) != 0) { cto = version == 0 ? (int)BE32(b2, q) : (int)BE32(b2, q); q += 4; }

                        bool key = Track.Handler == "soun" || (sflags & 0x00010000) == 0;
                        long local = dataAbs - absBase;
                        if (!dataAvailable)
                            output.Add(new Mp4Sample([], 0, 0, decodeTime, decodeTime + cto, dur, key));
                        else if (local >= 0 && local + size <= data.Length)
                            output.Add(new Mp4Sample(data, (int)local, (int)size, decodeTime, decodeTime + cto, dur, key));
                        dataAbs += size;
                        decodeTime += dur;
                    }
                    nextDataAbs = dataAbs;
                }
            }
        }
    }

    private IEnumerable<Mp4Sample> ReadProgressive(TimeSpan start, CancellationToken ct)
    {
        var t = Track;
        int n = t.Offsets!.Length;
        long target = t.FromTime(start);
        int first = 0;
        for (int i = 0; i < n; i++)
        {
            if (t.Dts![i] > target) break;
            if (t.Sync == null || t.Sync[i]) first = i;
        }

        byte[] block = [];
        long blockStart = 0;
        const int BlockSize = 1 << 20;
        for (int i = first; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            long off = t.Offsets[i];
            int size = t.Sizes![i];
            if (off < blockStart || off + size > blockStart + block.Length)
            {
                blockStart = off;
                block = _http.Read(off, Math.Max(BlockSize, size), ct);
                if (block.Length < size) yield break;
            }
            long dts = t.Dts![i];
            long pts = dts + (t.Cto?[i] ?? 0);
            long dur = i + 1 < n ? t.Dts[i + 1] - dts : 0;
            yield return new Mp4Sample(block, (int)(off - blockStart), size, dts, pts, dur, t.Sync == null || t.Sync[i]);
        }
    }

    // ---------------- Pomocnicze ----------------

    private static uint BE32(ReadOnlySpan<byte> b, int p) => p + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b[p..]) : 0;
    private static ulong BE64(ReadOnlySpan<byte> b, int p) => p + 8 <= b.Length ? BinaryPrimitives.ReadUInt64BigEndian(b[p..]) : 0;

    private readonly ref struct Box
    {
        public readonly string Type;
        public readonly ReadOnlySpan<byte> Body;
        public Box(string type, ReadOnlySpan<byte> body) { Type = type; Body = body; }
        public void Deconstruct(out string type, out ReadOnlySpan<byte> body) { type = Type; body = Body; }
    }

    private ref struct BoxEnumerator
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        public Box Current { get; private set; }

        public BoxEnumerator(ReadOnlySpan<byte> data) { _data = data; _pos = 0; Current = default; }
        public BoxEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_pos + 8 > _data.Length) return false;
            long size = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
            string type = Encoding.ASCII.GetString(_data.Slice(_pos + 4, 4));
            int hl = 8;
            if (size == 1)
            {
                if (_pos + 16 > _data.Length) return false;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(_data[(_pos + 8)..]);
                hl = 16;
            }
            else if (size == 0) size = _data.Length - _pos;
            if (size < hl || _pos + size > _data.Length) size = _data.Length - _pos;
            if (size < hl) return false;
            Current = new Box(type, _data.Slice(_pos + hl, (int)size - hl));
            _pos += (int)size;
            return true;
        }
    }

    private static BoxEnumerator Boxes(ReadOnlySpan<byte> data) => new(data);
}
