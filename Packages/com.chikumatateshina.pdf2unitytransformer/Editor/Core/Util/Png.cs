using System;
using System.IO;
using System.IO.Compression;

namespace PDF2Unity.Core.Util
{
    /// <summary>Minimal deterministic PNG encoder (RGBA8 / RGB8, filter type Up).</summary>
    public static class Png
    {
        static readonly uint[] CrcTable = MakeCrcTable();

        static uint[] MakeCrcTable()
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

        static uint Crc(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            WriteBE(s, (uint)data.Length);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            WriteBE(s, Crc(t, data));
        }

        static void WriteBE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        /// <summary>Encodes pixels (rows top to bottom). channels = 3 (RGB) or 4 (RGBA).</summary>
        public static byte[] Encode(byte[] pixels, int width, int height, int channels)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
                ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
                ihdr[8] = 8;
                ihdr[9] = (byte)(channels == 4 ? 6 : 2);
                Chunk(ms, "IHDR", ihdr);
                int row = width * channels;
                using (var raw = new MemoryStream())
                {
                    raw.WriteByte(0x78); raw.WriteByte(0x9C); // zlib header
                    uint a = 1, b = 0;
                    using (var z = new DeflateStream(raw, (long)width * height > 1_000_000 ? CompressionLevel.Fastest : CompressionLevel.Optimal, true))
                    {
                        var line = new byte[row + 1];
                        for (int y = 0; y < height; y++)
                        {
                            line[0] = 2; // Up
                            int o = y * row;
                            for (int x = 0; x < row; x++)
                                line[x + 1] = (byte)(pixels[o + x] - (y > 0 ? pixels[o - row + x] : 0));
                            z.Write(line, 0, line.Length);
                            foreach (var v in line)
                            {
                                a = (a + v) % 65521;
                                b = (b + a) % 65521;
                            }
                        }
                    }
                    WriteBE(raw, (b << 16) | a);
                    Chunk(ms, "IDAT", raw.ToArray());
                }
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }
    }
}
