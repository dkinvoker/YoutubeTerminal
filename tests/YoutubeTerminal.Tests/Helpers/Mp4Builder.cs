using System.Buffers.Binary;
using System.Text;

namespace YoutubeTerminal.Tests.Helpers;

/// <summary>Budowanie syntetycznych plików MP4 (fragmentowanych DASH i klasycznych) do testów demuksera.</summary>
internal static class Mp4Builder
{
    public static byte[] Box(string type, params byte[][] children)
    {
        int len = 8 + children.Sum(c => c.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)len);
        Encoding.ASCII.GetBytes(type, 0, 4, b, 4);
        int p = 8;
        foreach (var c in children) { c.CopyTo(b, p); p += c.Length; }
        return b;
    }

    public static byte[] FullBox(string type, byte version, uint flags, params byte[][] payload)
    {
        var vf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(vf, ((uint)version << 24) | (flags & 0xFFFFFF));
        return Box(type, [vf, .. payload]);
    }

    public static byte[] U8(int v) => [(byte)v];
    public static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }
    public static byte[] U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }
    public static byte[] U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); return b; }
    public static byte[] Zeros(int n) => new byte[n];
    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static readonly byte[] Sps = [0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9];
    public static readonly byte[] Pps = [0x68, 0xEB, 0xE3, 0xCB];

    public static byte[] AvcC() => Concat(
        U8(1), U8(0x64), U8(0x00), U8(0x1F), U8(0xFF), // lengthSizeMinusOne = 3
        U8(0xE1), U16(Sps.Length), Sps,
        U8(1), U16(Pps.Length), Pps);

    /// <summary>Pozycja avc1 w stsd (VisualSampleEntry, 78 bajtów nagłówka + avcC).</summary>
    public static byte[] Avc1(int width, int height)
    {
        var entry = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);        // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(74), 0x18);    // depth
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(76), 0xFFFF);  // pre_defined
        return Box("avc1", entry, Box("avcC", AvcC()));
    }

    /// <summary>Pozycja mp4a w stsd z esds zawierającym AudioSpecificConfig.</summary>
    public static byte[] Mp4a(int channels, int sampleRate, byte[] asc)
    {
        var entry = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(16), (ushort)channels);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(18), 16);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24), (uint)sampleRate << 16);

        var dsi = Concat(U8(0x05), U8(asc.Length), asc);
        var dcd = Concat(U8(0x04), U8(13 + dsi.Length), U8(0x40), U8(0x15), Zeros(3), U32(128000), U32(128000), dsi);
        var sl = Concat(U8(0x06), U8(1), U8(2));
        var es = Concat(U8(0x03), U8(3 + dcd.Length + sl.Length), U16(1), U8(0), dcd, sl);
        return Box("mp4a", entry, FullBox("esds", 0, 0, es));
    }

    public static byte[] Trak(uint trackId, uint timescale, uint duration, string handler, byte[] sampleEntry, byte[] stblTables)
    {
        var tkhd = FullBox("tkhd", 0, 3, U32(0), U32(0), U32(trackId), Zeros(68));
        var mdhd = FullBox("mdhd", 0, 0, U32(0), U32(0), U32(timescale), U32(duration), Zeros(4));
        var hdlr = FullBox("hdlr", 0, 0, U32(0), Ascii(handler), Zeros(12), Ascii("test\0"));
        var stsd = FullBox("stsd", 0, 0, U32(1), sampleEntry);
        var stbl = Box("stbl", stsd, stblTables);
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", mdhd, hdlr, minf);
        return Box("trak", tkhd, mdia);
    }

    public static byte[] EmptyTables() => Concat(
        FullBox("stts", 0, 0, U32(0)),
        FullBox("stsc", 0, 0, U32(0)),
        FullBox("stsz", 0, 0, U32(0), U32(0)),
        FullBox("stco", 0, 0, U32(0)));

    public static byte[] Ftyp() => Box("ftyp", Ascii("iso6"), U32(0), Ascii("iso6dash"));

    public static byte[] Mvhd() => FullBox("mvhd", 0, 0, U32(0), U32(0), U32(1000), U32(0), Zeros(80));

    // ---------------- Fragmentowany (DASH) ----------------

    public sealed record FragSample(byte[] Data, uint Duration, int Cto, bool Key);

    /// <summary>
    /// Plik DASH: ftyp, moov (avc1, mvex/trex), sidx, a potem segmenty moof+mdat.
    /// Każdy segment to lista próbek; czasy w skali <paramref name="timescale"/>.
    /// </summary>
    public static byte[] FragmentedVideo(uint timescale, int width, int height, List<List<FragSample>> segments)
    {
        var moov = Box("moov", Mvhd(),
            Trak(1, timescale, 0, "vide", Avc1(width, height), EmptyTables()),
            Box("mvex", FullBox("trex", 0, 0, U32(1), U32(1), U32(0), U32(0), U32(0))));

        var segBytes = new List<byte[]>();
        long decodeTime = 0;
        uint seq = 1;
        var durations = new List<long>();
        foreach (var seg in segments)
        {
            segBytes.Add(Fragment(seq++, decodeTime, seg));
            long d = seg.Sum(s => (long)s.Duration);
            durations.Add(d);
            decodeTime += d;
        }

        var refs = new List<byte[]>();
        for (int i = 0; i < segBytes.Count; i++)
            refs.Add(Concat(U32(segBytes[i].Length & 0x7FFFFFFF), U32(durations[i]), U32(0x90000000)));
        var sidx = FullBox("sidx", 0, 0, U32(1), U32(timescale), U32(0), U32(0), U16(0), U16(refs.Count), Concat(refs.ToArray()));

        return Concat([Ftyp(), moov, sidx, .. segBytes]);
    }

    public static byte[] Fragment(uint sequence, long baseDecodeTime, List<FragSample> samples)
    {
        // trun: data-offset + czas + rozmiar + flagi + przesunięcie kompozycji
        const uint trunFlags = 0x01 | 0x100 | 0x200 | 0x400 | 0x800;
        byte[] BuildMoof(int dataOffset)
        {
            var entries = samples.SelectMany(s => Concat(
                U32(s.Duration), U32(s.Data.Length), U32(s.Key ? 0x02000000 : 0x01010000), U32(s.Cto))).ToArray();
            var trun = FullBox("trun", 0, trunFlags, U32(samples.Count), U32(dataOffset), entries);
            var tfhd = FullBox("tfhd", 0, 0x020000, U32(1));
            var tfdt = FullBox("tfdt", 1, 0, U64(baseDecodeTime));
            var traf = Box("traf", tfhd, tfdt, trun);
            return Box("moof", FullBox("mfhd", 0, 0, U32(sequence)), traf);
        }
        int moofSize = BuildMoof(0).Length;
        var moof = BuildMoof(moofSize + 8);
        var mdat = Box("mdat", Concat(samples.Select(s => s.Data).ToArray()));
        return Concat(moof, mdat);
    }

    /// <summary>Próbka AVCC: długość (4 bajty) + jedna jednostka NAL.</summary>
    public static byte[] AvccNal(params byte[] nal) => Concat(U32(nal.Length), nal);

    // ---------------- Klasyczny (progresywny) z tablicami próbek ----------------

    /// <summary>
    /// Plik ftyp + moov (jedna ścieżka audio mp4a z stts/stsc/stsz/stco) + mdat.
    /// Próbki ułożone w kawałki po <paramref name="perChunk"/>.
    /// </summary>
    public static byte[] ProgressiveAudio(uint timescale, int sampleRate, int channels, byte[] asc, List<byte[]> samples, uint delta, int perChunk)
    {
        int chunks = (samples.Count + perChunk - 1) / perChunk;
        byte[] BuildMoov(long dataStart)
        {
            var offsets = new List<long>();
            long off = dataStart;
            for (int c = 0; c < chunks; c++)
            {
                offsets.Add(off);
                off += samples.Skip(c * perChunk).Take(perChunk).Sum(s => s.Length);
            }
            var tables = Concat(
                FullBox("stts", 0, 0, U32(1), U32(samples.Count), U32(delta)),
                FullBox("stsc", 0, 0, U32(1), U32(1), U32(perChunk), U32(1)),
                FullBox("stsz", 0, 0, U32(0), U32(samples.Count), Concat(samples.Select(s => U32(s.Length)).ToArray())),
                FullBox("stco", 0, 0, U32(offsets.Count), Concat(offsets.Select(o => U32(o)).ToArray())));
            return Box("moov", Mvhd(),
                Trak(1, timescale, (uint)(delta * samples.Count), "soun", Mp4a(channels, sampleRate, asc), tables));
        }
        var ftyp = Ftyp();
        int moovSize = BuildMoov(0).Length;
        var moov = BuildMoov(ftyp.Length + moovSize + 8);
        var mdat = Box("mdat", Concat(samples.ToArray()));
        return Concat(ftyp, moov, mdat);
    }
}
