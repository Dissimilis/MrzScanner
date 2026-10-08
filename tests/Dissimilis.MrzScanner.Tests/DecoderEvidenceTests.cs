using System.IO.Compression;
using System.Text;
using Dissimilis.MrzScanner.Internal;
using StbImageSharp;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class DecoderEvidenceTests
{
    [Fact]
    public void Grayscale_alpha_decode_preserves_rgba_compositing()
    {
        var pixels = new byte[256 * 4];
        new Random(912).NextBytes(pixels);
        byte[] png = Png(pixels, 16, 16);
        ImageResult old = ImageResult.FromMemory(png, ColorComponents.RedGreenBlueAlpha);
        byte[] expected = GrayImage.FromRgba(old.Data, old.Width, old.Height).Pixels;
        var decoded = ImageDecoder.Decode(png);
        Assert.Equal(ImageDecoder.Status.Ok, decoded.Status);
        Assert.Equal(expected, decoded.Image!.Pixels);
    }

    [Fact]
    public void Grayscale_alpha_decode_allocates_less_than_rgba()
    {
        byte[] bmp = SyntheticMrz.ToBmp(new byte[1024 * 1024], 1024, 1024);
        ImageDecoder.Decode(bmp);
        ImageResult.FromMemory(bmp, ColorComponents.RedGreenBlueAlpha);
        long start = GC.GetAllocatedBytesForCurrentThread();
        ImageResult old = ImageResult.FromMemory(bmp, ColorComponents.RedGreenBlueAlpha);
        GrayImage.FromRgba(old.Data, old.Width, old.Height);
        long previous = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        ImageDecoder.Decode(bmp);
        long current = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(current < previous - 1024 * 1024, $"Old {previous}, new {current}");
    }

    private static byte[] Png(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        static byte[] BigEndian(int value) => new[]
            { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        void Chunk(string type, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(type);
            output.Write(BigEndian(data.Length));
            output.Write(name);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte b in name.Concat(data))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            output.Write(BigEndian(unchecked((int)~crc)));
        }
        Chunk("IHDR", BigEndian(width).Concat(BigEndian(height)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray());
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            for (int y = 0; y < height; y++)
            {
                zip.WriteByte(0);
                zip.Write(rgba, y * width * 4, width * 4);
            }
        }
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }
}
