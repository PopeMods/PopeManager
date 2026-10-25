namespace PopeManager;

internal static class SteamQr
{
        internal static byte[] BuildQrPng(byte[][] grid)
        {
            int sizeY = grid.Length;
            int sizeX = grid.Max(r => r.Length);
            const int quiet = 3;
            int dimX = sizeX + quiet * 2;
            int dimY = sizeY + quiet * 2;

            var raw = new byte[(dimX + 1) * dimY];
            int pos = 0;
            for (int y = 0; y < dimY; y++)
            {
                raw[pos++] = 0; // no filtering
                for (int x = 0; x < dimX; x++)
                {
                    int my = y - quiet, mx = x - quiet;
                    bool dark = my >= 0 && my < sizeY && mx >= 0 && mx < sizeX
                        && mx < grid[my].Length && grid[my][mx] == 1;
                    raw[pos++] = dark ? (byte)0 : (byte)255;
                }
            }

            var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            var ihdr = new List<byte>();
            BE(ihdr, dimX); BE(ihdr, dimY);
            ihdr.AddRange(new byte[] { 8, 0, 0, 0, 0 }); // 8 bit grayscale
            WriteChunk(png, "IHDR", ihdr.ToArray());

            using var compressed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true)) zlib.Write(raw);
            WriteChunk(png, "IDAT", compressed.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();

            static void BE(List<byte> l, int v)
            {
                l.Add((byte)(v >> 24));
                l.Add((byte)(v >> 16));
                l.Add((byte)(v >> 8));
                l.Add((byte)v);
            }

            static void WriteChunk(List<byte> png, string type, byte[] data)
            {
                BE(png, data.Length);
                var t = System.Text.Encoding.ASCII.GetBytes(type);
                png.AddRange(t);
                png.AddRange(data);
                uint crc = Crc32(t.Concat(data).ToArray());
                png.AddRange(new byte[]
                {
                    (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc
                });
            }

            static uint Crc32(byte[] d)
            {
                uint c = 0xFFFFFFFFu;
                foreach (var b in d)
                {
                    c ^= b;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                return c ^ 0xFFFFFFFFu;
            }

        }
}
