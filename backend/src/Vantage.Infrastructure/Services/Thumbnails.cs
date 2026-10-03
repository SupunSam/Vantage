using System.Buffers.Binary;
using Vantage.Domain;

namespace Vantage.Infrastructure.Services;

public sealed record ThumbnailInfo(string Extension, string ContentType, int Width, int Height);

/// <summary>
/// Thumbnail rules from the requirements: PNG or JPG, up to 1 MB, shown at 640 x 360.
/// Any 16:9 image of at least 640 x 360 is accepted (a 1280 x 720 screenshot is fine); the file type is checked
/// from its content, not its name.
/// </summary>
public static class Thumbnails
{
    public static ThumbnailInfo Validate(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) throw new RuleException("The thumbnail file is empty.");
        if (data.Length > Rules.ThumbnailMaxBytes) throw new RuleException("The thumbnail is larger than 1 MB. Export it smaller (a 640 x 360 PNG or JPG is usually under 200 KB).");

        var info = ProbePng(data) ?? ProbeJpeg(data)
            ?? throw new RuleException("The thumbnail must be a PNG or JPG image.");

        if (info.Width < Rules.ThumbnailWidth || info.Height < Rules.ThumbnailHeight)
            throw new RuleException($"The thumbnail is {info.Width} x {info.Height} px; it needs to be at least {Rules.ThumbnailWidth} x {Rules.ThumbnailHeight} px.");
        var ratio = (double)info.Width / info.Height;
        if (Math.Abs(ratio - 16.0 / 9.0) > 0.03)
            throw new RuleException($"The thumbnail is {info.Width} x {info.Height} px; it needs a 16:9 shape, such as 640 x 360 or 1280 x 720.");
        return info;
    }

    private static ThumbnailInfo? ProbePng(ReadOnlySpan<byte> d)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length < 24 || !d[..8].SequenceEqual(sig)) return null;
        // The IHDR chunk always comes first: width and height are big-endian at bytes 16 and 20.
        return new ThumbnailInfo(".png", "image/png", BinaryPrimitives.ReadInt32BigEndian(d[16..]), BinaryPrimitives.ReadInt32BigEndian(d[20..]));
    }

    private static ThumbnailInfo? ProbeJpeg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            // Start-of-frame markers (baseline, progressive, …) carry the size; C4, C8 and CC are not frames.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                int height = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]);
                return new ThumbnailInfo(".jpg", "image/jpeg", width, height);
            }
            i += 2 + length;
        }
        return null;
    }

    public static string ContentTypeFor(string key) =>
        key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}
