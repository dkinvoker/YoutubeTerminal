using System.Buffers.Binary;
using System.IO.Compression;

namespace YoutubeTerminal.Graphics;

/// <summary>Minimalny koder PNG (RGB 8-bit) — do zrzutów ekranu i diagnostyki.</summary>
internal static class Png
{
    public static byte[] Encode(uint[] px, int w, int h)
    {
        using var raw = new MemoryStream(w * h * 3 + h);
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
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
            raw.WriteTo(zs);

        using var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", z.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, byte[] body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        o.Write(len);
        var tb = System.Text.Encoding.ASCII.GetBytes(type);
        o.Write(tb);
        o.Write(body);
        uint crc = Crc32(tb, 0xFFFFFFFF);
        crc = Crc32(body, crc) ^ 0xFFFFFFFF;
        Span<byte> cb = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(cb, crc);
        o.Write(cb);
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
