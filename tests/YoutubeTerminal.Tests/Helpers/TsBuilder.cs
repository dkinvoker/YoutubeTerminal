using System.Buffers.Binary;
using System.Text;

namespace YoutubeTerminal.Tests.Helpers;

/// <summary>Budowanie segmentów MPEG-TS i „packed audio” (ID3 + ADTS) dla testów HLS.</summary>
internal static class TsBuilder
{
    public const int PmtPid = 0x1000;
    public const int VideoPid = 0x100;
    public const int AudioPid = 0x101;

    public static byte[] Segment(params (int Pid, int StreamType, byte[] Payload, long Pts)[] pes)
    {
        var packets = new List<byte[]>();
        var cc = new Dictionary<int, int>();
        packets.Add(Psi(0, Pat()));
        packets.Add(Psi(PmtPid, Pmt(pes.Select(p => (p.Pid, p.StreamType)).Distinct().ToArray())));
        foreach (var (pid, _, payload, pts) in pes)
            packets.AddRange(Packetize(pid, Pes(pid == AudioPid ? 0xC0 : 0xE0, payload, pts), cc));
        return packets.SelectMany(p => p).ToArray();
    }

    private static byte[] Pat()
    {
        // table_id, długość, tsid, wersja, numer sekcji, ostatnia, program 1 -> PMT, CRC (niesprawdzane)
        var s = new List<byte> { 0x00, 0xB0, 13, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01,
            (byte)(0xE0 | (PmtPid >> 8)), (byte)(PmtPid & 0xFF), 0, 0, 0, 0 };
        return s.ToArray();
    }

    private static byte[] Pmt((int Pid, int Type)[] streams)
    {
        int secLen = 9 + streams.Length * 5 + 4;
        var s = new List<byte> { 0x02, (byte)(0xB0 | (secLen >> 8)), (byte)secLen, 0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00 };
        foreach (var (pid, type) in streams)
            s.AddRange([(byte)type, (byte)(0xE0 | (pid >> 8)), (byte)pid, 0xF0, 0x00]);
        s.AddRange([0, 0, 0, 0]);
        return s.ToArray();
    }

    private static byte[] Psi(int pid, byte[] section)
    {
        var pkt = new byte[188];
        Array.Fill(pkt, (byte)0xFF);
        pkt[0] = 0x47;
        pkt[1] = (byte)(0x40 | (pid >> 8));
        pkt[2] = (byte)pid;
        pkt[3] = 0x10;
        pkt[4] = 0; // pointer_field
        section.CopyTo(pkt, 5);
        return pkt;
    }

    public static byte[] Pes(int streamId, byte[] payload, long pts)
    {
        var h = new byte[14];
        h[2] = 1;
        h[3] = (byte)streamId;
        // PES_packet_length = 0 (bez ograniczenia)
        h[6] = 0x80;
        h[7] = 0x80; // tylko PTS
        h[8] = 5;
        h[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        h[10] = (byte)(pts >> 22);
        h[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        h[12] = (byte)(pts >> 7);
        h[13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        return [.. h, .. payload];
    }

    private static IEnumerable<byte[]> Packetize(int pid, byte[] pes, Dictionary<int, int> ccs)
    {
        int pos = 0;
        bool first = true;
        while (pos < pes.Length)
        {
            int cc = ccs.TryGetValue(pid, out var c) ? c : 0;
            ccs[pid] = (cc + 1) & 0xF;
            var pkt = new byte[188];
            pkt[0] = 0x47;
            pkt[1] = (byte)((first ? 0x40 : 0) | (pid >> 8));
            pkt[2] = (byte)pid;
            int remaining = pes.Length - pos;
            if (remaining >= 184)
            {
                pkt[3] = (byte)(0x10 | cc);
                Array.Copy(pes, pos, pkt, 4, 184);
                pos += 184;
            }
            else
            {
                // Pole adaptacji z wypełnieniem, by ładunek kończył pakiet
                pkt[3] = (byte)(0x30 | cc);
                int afLen = 183 - remaining;
                pkt[4] = (byte)afLen;
                if (afLen > 0)
                {
                    pkt[5] = 0x00;
                    for (int i = 6; i < 5 + afLen; i++) pkt[i] = 0xFF;
                }
                Array.Copy(pes, pos, pkt, 5 + afLen, remaining);
                pos += remaining;
            }
            first = false;
            yield return pkt;
        }
    }

    // ---------------- Packed audio ----------------

    public static byte[] Id3Timestamp(long pts90k)
    {
        var owner = Encoding.ASCII.GetBytes("com.apple.streaming.transportStreamTimestamp\0");
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, (ulong)pts90k);
        int frameSize = owner.Length + data.Length;
        var frame = new List<byte>();
        frame.AddRange(Encoding.ASCII.GetBytes("PRIV"));
        frame.AddRange(Syncsafe(frameSize));
        frame.AddRange([0, 0]);
        frame.AddRange(owner);
        frame.AddRange(data);
        var tag = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0 };
        tag.AddRange(Syncsafe(frame.Count));
        tag.AddRange(frame);
        return tag.ToArray();
    }

    private static byte[] Syncsafe(int v) => [(byte)((v >> 21) & 0x7F), (byte)((v >> 14) & 0x7F), (byte)((v >> 7) & 0x7F), (byte)(v & 0x7F)];

    /// <summary>Ramka ADTS (bez CRC) z danym ładunkiem: AAC-LC, indeks częstotliwości, kanały.</summary>
    public static byte[] Adts(byte[] payload, int freqIdx = 4, int channels = 2)
    {
        int len = payload.Length + 7;
        var h = new byte[7];
        h[0] = 0xFF;
        h[1] = 0xF1; // MPEG-4, bez CRC
        h[2] = (byte)((1 << 6) | (freqIdx << 2) | ((channels >> 2) & 1)); // profil 1 = AAC LC
        h[3] = (byte)(((channels & 3) << 6) | ((len >> 11) & 3));
        h[4] = (byte)(len >> 3);
        h[5] = (byte)(((len & 7) << 5) | 0x1F);
        h[6] = 0xFC;
        return [.. h, .. payload];
    }
}
