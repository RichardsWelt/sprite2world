using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sprite2World.Infrastructure;

public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(Signature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("The file is not a valid PNG image.");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192) throw new InvalidDataException("PNG dimensions are outside the allowed range.");
        return (width, height);
    }

    public static byte[] EncodeRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8; header[9] = 6;
        WriteChunk(output, "IHDR", header);
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var p = pixel(x, y);
                raw.WriteByte(p.R); raw.WriteByte(p.G); raw.WriteByte(p.B); raw.WriteByte(p.A);
            }
        }
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) raw.CopyTo(zlib);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type); stream.Write(typeBytes); stream.Write(data);
        var crcData = new byte[typeBytes.Length + data.Length]; typeBytes.CopyTo(crcData, 0); data.CopyTo(crcData.AsSpan(typeBytes.Length));
        Span<byte> crc = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcData)); stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
