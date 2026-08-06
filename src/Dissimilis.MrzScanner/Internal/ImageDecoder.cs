using StbImageSharp;

namespace Dissimilis.MrzScanner.Internal;

/// <summary>Decodes encoded images (JPEG, PNG, BMP) into grayscale via StbImageSharp.</summary>
internal static class ImageDecoder
{
    /// <summary>
    /// Decoding is refused above this pixel count so a decompression bomb or an
    /// absurd scan cannot exhaust memory. 150 megapixels is far beyond any
    /// sensible document photo.
    /// </summary>
    private const long MaxPixels = 150_000_000;

    /// <summary>Outcome of a decode attempt.</summary>
    public enum Status
    {
        Ok,
        NotAnImage,
        TooLarge,
        UnsupportedWebP,
    }

    /// <summary>Decodes image bytes into grayscale.</summary>
    public static (GrayImage? Image, Status Status) Decode(byte[] data)
    {
        // WebP is common on the web and often hides behind a .jpg extension;
        // give it a precise diagnosis instead of a generic decode failure.
        if (data.Length >= 12 &&
            data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return (null, Status.UnsupportedWebP);
        }

        try
        {
            // Probe the header first so oversized images are rejected before
            // any pixel buffer is allocated.
            ImageInfo? info;
            using (var stream = new MemoryStream(data, writable: false))
            {
                info = ImageInfo.FromStream(stream);
            }
            if (info is null)
                return (null, Status.NotAnImage);
            if ((long)info.Value.Width * info.Value.Height > MaxPixels)
                return (null, Status.TooLarge);

            ImageResult image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            return (GrayImage.FromRgba(image.Data, image.Width, image.Height), Status.Ok);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // StbImageSharp throws on malformed input; the reader reports it as an issue instead.
            return (null, Status.NotAnImage);
        }
    }
}
