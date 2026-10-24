using System.Text;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>The styled words of a comment (<see cref="CommentFormat.Parse"/>) as Pango markup, so that a label shows them. Nothing the user typed is ever markup: it is escaped.</summary>
internal static class RichText
{
    public static string Markup(IEnumerable<CommentSpan> spans)
    {
        var markup = new StringBuilder();
        foreach (var span in spans)
        {
            var text = GLib.Functions.MarkupEscapeText(span.Text, -1);
            var open = new StringBuilder();
            var close = new StringBuilder();
            void Wrap(CommentStyle style, string tag)
            {
                if (!span.Style.HasFlag(style)) return;

                open.Append('<').Append(tag).Append('>');
                close.Insert(0, $"</{tag}>");
            }

            Wrap(CommentStyle.Bold, "b");
            Wrap(CommentStyle.Italic, "i");
            Wrap(CommentStyle.Underline, "u");
            Wrap(CommentStyle.Strikethrough, "s");
            markup.Append(open).Append(text).Append(close);
        }

        return markup.ToString();
    }

    /// <summary>A label that shows the spans, wrapped, selectable.</summary>
    public static Gtk.Label Label(IEnumerable<CommentSpan> spans)
    {
        var label = Ui.Label("");
        label.UseMarkup = true;
        label.SetMarkup(Markup(spans));
        label.Selectable = true;
        return label;
    }

    /// <summary>Text offsets: GTK counts characters (code points), .NET counts UTF-16 units; they differ for what lies outside the basic plane (emoji).</summary>
    public static int ToUtf16(string text, int characterOffset)
    {
        var index = 0;
        for (var seen = 0; seen < characterOffset && index < text.Length; seen++) index += char.IsHighSurrogate(text[index]) ? 2 : 1;
        return Math.Min(index, text.Length);
    }

    public static int ToCharacters(string text, int utf16Index)
    {
        var count = 0;
        for (var index = 0; index < utf16Index && index < text.Length; count++) index += char.IsHighSurrogate(text[index]) ? 2 : 1;
        return count;
    }
}
