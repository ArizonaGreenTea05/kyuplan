using System.Text.RegularExpressions;
using System.Xml;

namespace Kyuplan.Client;

/// <summary>
/// Whether an SVG is safe to hand to the renderer. A browser draws an SVG that is shown as an image from what the file itself carries and
/// loads nothing else; the renderer of the desktop client would also fetch what the file points at (<c>file:</c> paths, web addresses).
/// The logo is uploaded by an administrator and drawn on every user's machine, so such a file is refused instead. No WPF type, so that
/// it can be tested outside the Windows desktop framework.
/// </summary>
public static partial class SvgSafety
{
    /// <summary>
    /// <c>true</c> when the text is well-formed XML whose references (<c>href</c>, <c>url(...)</c>, <c>@import</c>) all stay inside the
    /// document (<c>#id</c>) or are data in the file itself (<c>data:</c>). A document that cannot be read is not self-contained either.
    /// </summary>
    public static bool IsSelfContained(string svg)
    {
        try
        {
            // The doctype is skipped (not followed), so that nothing it declares is fetched or expanded.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(svg), settings);

            var inStyle = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        inStyle = reader.LocalName == "style" && !reader.IsEmptyElement;

                        // A hyperlink (<a>) is only followed when somebody clicks it, which nothing here does.
                        var fetches = reader.LocalName != "a";
                        while (reader.MoveToNextAttribute())
                        {
                            var isLink = reader.LocalName == "href";
                            if (isLink ? fetches && !IsLocal(reader.Value) : PointsOutside(reader.Value)) return false;
                        }

                        reader.MoveToElement();
                        break;

                    case XmlNodeType.EndElement:
                        inStyle = false;
                        break;

                    case XmlNodeType.Text or XmlNodeType.CDATA when inStyle && PointsOutside(reader.Value):
                        return false; // the text of a <text> is only text, however much it looks like a reference
                }
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsLocal(string reference)
    {
        var text = reference.Trim();
        return text.StartsWith('#') || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A <c>url(...)</c> that is not <c>#id</c> or <c>data:</c>, or an <c>@import</c>: in a style attribute, a style element or a paint.</summary>
    private static bool PointsOutside(string text) => text.Contains("url(", StringComparison.OrdinalIgnoreCase) && ExternalUrl().IsMatch(text)
        || text.Contains("@import", StringComparison.OrdinalIgnoreCase);

    // The atomic group matters: without it the regex gives back the quote and the blanks to find a position that is not "#...".
    [GeneratedRegex("""url\((?>\s*['"]?\s*)(?!#|data:)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();
}
