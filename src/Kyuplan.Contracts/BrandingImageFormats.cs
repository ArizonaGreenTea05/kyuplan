using System.Text;

namespace Kyuplan.Contracts;

/// <summary>
/// What an uploaded file really is. The content type a client claims is not believed: it is the bytes that decide, so that nothing is
/// stored and served as an image that is none (and an SVG is the only one of them that can carry more than pixels).
/// </summary>
public static class BrandingImageFormats
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] IcoSignature = [0x00, 0x00, 0x01, 0x00];
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>The content type the bytes have, or <c>null</c> when they are none of the formats a branding image can be.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature)) return BrandingSlots.Png;
        if (data.StartsWith(JpegSignature)) return BrandingSlots.Jpeg;
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return BrandingSlots.Gif;
        if (data.StartsWith("BM"u8) && data.Length > 14) return BrandingSlots.Bmp;
        if (data.Length > 6 && data.StartsWith(IcoSignature)) return BrandingSlots.Ico;

        return LooksLikeSvg(data) ? BrandingSlots.Svg : null;
    }

    /// <summary>"PNG", "SVG", ...: for a message that says which formats a slot takes.</summary>
    public static string Name(string contentType) => contentType switch
    {
        BrandingSlots.Svg => "SVG",
        BrandingSlots.Ico => "ICO",
        _ => contentType["image/".Length..].ToUpperInvariant(),
    };

    // An SVG is text whose first element is <svg>; an XML declaration, comments and a doctype may come before it.
    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var head = data[..Math.Min(data.Length, 4096)];
        if (head.StartsWith(Utf8Bom)) head = head[Utf8Bom.Length..];

        var rest = Encoding.UTF8.GetString(head).AsSpan().TrimStart();
        while (rest.Length > 0 && rest[0] == '<')
        {
            if (rest.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
                return rest.Length > 4 && (char.IsWhiteSpace(rest[4]) || rest[4] is '>' or '/');

            var closing = rest.StartsWith("<!--", StringComparison.Ordinal) ? "-->" : ">";
            var isPrologue = rest.StartsWith("<?", StringComparison.Ordinal) || rest.StartsWith("<!", StringComparison.Ordinal);
            var end = rest.IndexOf(closing, StringComparison.Ordinal);
            if (!isPrologue || end < 0) return false;

            rest = rest[(end + closing.Length)..].TrimStart();
        }

        return false;
    }
}
