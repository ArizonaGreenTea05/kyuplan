using System.Text;

namespace Kyuplan.Contracts;

/// <summary>How a stretch of a comment is styled. Styles combine.</summary>
[Flags]
public enum CommentStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
}

/// <summary>A stretch of a comment's text that is styled one way. A new line is a <c>\n</c> in the text.</summary>
public sealed record CommentSpan(string Text, CommentStyle Style);

/// <summary>What the text is after a style was applied, and what is selected in it then.</summary>
public readonly record struct TextChange(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// The markup a comment is written in: a few styles that cannot be mistaken for ordinary text. <c>**bold**</c>, <c>*italic*</c>,
/// <c>__underlined__</c> and <c>~~crossed out~~</c>, which combine and may run over several lines. A marker only counts when it has its
/// partner and sits at the edge of the text it styles (<c>5 * 3 * 2</c> is not italic); one that does not stays as it is. A backslash
/// writes a marker character as text (<c>\*</c>). Every client turns it into styled text with <see cref="Parse"/>, so none renders
/// HTML and nothing a user types can inject markup.
/// </summary>
public static class CommentFormat
{
    private const string Escapable = "\\*_~";

    private static readonly CommentStyle[] Styles = [CommentStyle.Bold, CommentStyle.Italic, CommentStyle.Underline, CommentStyle.Strikethrough];

    private enum Role
    {
        Text,
        Opens,
        Closes,
    }

    /// <summary>The marker of a style as written in a comment.</summary>
    public static string Marker(CommentStyle style) => style switch
    {
        CommentStyle.Bold => "**",
        CommentStyle.Italic => "*",
        CommentStyle.Underline => "__",
        CommentStyle.Strikethrough => "~~",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "A single style is expected."),
    };

    /// <summary>What is stored for what a user typed: line breaks as <c>\n</c> and no blank space around it all.</summary>
    public static string Clean(string? body) => (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>The comment as styled stretches of text, in order. Neighbours of the same style are one stretch.</summary>
    public static IReadOnlyList<CommentSpan> Parse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return [];

        var items = Scan(body);
        var roles = Pair(items);

        var spans = new List<CommentSpan>();
        var text = new StringBuilder();
        var style = CommentStyle.None;

        void Flush()
        {
            if (text.Length == 0) return;

            if (spans.Count > 0 && spans[^1].Style == style)
                spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
            else
                spans.Add(new CommentSpan(text.ToString(), style));
            text.Clear();
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Marker == CommentStyle.None)
            {
                text.Append(item.Char);
            }
            else if (roles[i] == Role.Text)
            {
                text.Append(Marker(item.Marker)); // a marker without a partner is what the user typed
            }
            else
            {
                Flush();
                style = roles[i] == Role.Opens ? style | item.Marker : style & ~item.Marker;
            }
        }

        Flush();
        return spans;
    }

    /// <summary>The words of the comment without any styling markers.</summary>
    public static string ToPlainText(string? body) => string.Concat(Parse(body).Select(s => s.Text));

    /// <summary>
    /// How a body was changed into another: the words changed, or the words are the same and only the styling differs (or neither).
    /// </summary>
    public static (bool TextChanged, bool FormattingChanged) Compare(string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return (false, false);

        var textChanged = !string.Equals(ToPlainText(before), ToPlainText(after), StringComparison.Ordinal);
        return (textChanged, !textChanged);
    }

    /// <summary>
    /// What a toolbar button does: puts the style on the selected text, or takes it off again when the text has it already (the markers
    /// are right around it or at both of its ends). Blank space at the ends of the selection stays outside the markers, which would not
    /// count otherwise. Nothing selected inserts a pair of markers with the cursor between them.
    /// </summary>
    public static TextChange Toggle(string text, int selectionStart, int selectionLength, CommentStyle style)
    {
        var marker = Marker(style);
        var width = marker.Length;
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var length = Math.Clamp(selectionLength, 0, text.Length - start);
        var end = start + length;

        // The style is on already: its markers are right around the selection ...
        if (IsOn(TrailingRun(text, start, marker[0]), LeadingRun(text, end, marker[0]), width))
            return new TextChange(text.Remove(end, width).Remove(start - width, width), start - width, length);

        // ... or are its own first and last characters.
        var selected = text.Substring(start, length);
        var leading = LeadingRun(selected, 0, marker[0]);
        var trailing = TrailingRun(selected, selected.Length, marker[0]);
        if (leading + trailing <= length && IsOn(leading, trailing, width))
            return new TextChange(text[..start] + selected[width..^width] + text[end..], start, length - 2 * width);

        var blankBefore = selected.TakeWhile(char.IsWhiteSpace).Count();
        var blankAfter = selected.Reverse().TakeWhile(char.IsWhiteSpace).Count();
        if (blankBefore + blankAfter >= length) return new TextChange(text.Insert(start, marker + marker), start + width, 0);

        var coreStart = start + blankBefore;
        var coreLength = length - blankBefore - blankAfter;
        var styled = text[..coreStart] + marker + text.Substring(coreStart, coreLength) + marker + text[(coreStart + coreLength)..];
        return new TextChange(styled, coreStart + width, coreLength);
    }

    /// <summary>
    /// Whether a run of marker characters before and one after a stretch of text make the style. Bold, underline and strikethrough need
    /// two on each side; italic needs an odd number, so that <c>**bold**</c> is not italic but <c>***both***</c> is.
    /// </summary>
    private static bool IsOn(int before, int after, int width) =>
        width == 1 ? before % 2 == 1 && after % 2 == 1 : before >= width && after >= width;

    /// <summary>How many times <paramref name="c"/> comes right before <paramref name="index"/>.</summary>
    private static int TrailingRun(string text, int index, char c)
    {
        var count = 0;
        while (index - count - 1 >= 0 && text[index - count - 1] == c) count++;
        return count;
    }

    /// <summary>How many times <paramref name="c"/> comes right from <paramref name="index"/> on.</summary>
    private static int LeadingRun(string text, int index, char c)
    {
        var count = 0;
        while (index + count < text.Length && text[index + count] == c) count++;
        return count;
    }

    private readonly record struct Item(char Char, CommentStyle Marker);

    /// <summary>Splits the body into characters and marker candidates; an escaped marker character is just a character.</summary>
    private static List<Item> Scan(string body)
    {
        var items = new List<Item>(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            var next = i + 1 < body.Length ? body[i + 1] : '\0';

            if (c == '\\' && Escapable.Contains(next))
            {
                items.Add(new Item(next, CommentStyle.None));
                i++;
            }
            else if (c == '*' && next == '*')
            {
                items.Add(new Item(c, CommentStyle.Bold));
                i++;
            }
            else if (c == '*')
            {
                items.Add(new Item(c, CommentStyle.Italic));
            }
            else if (c == '_' && next == '_')
            {
                items.Add(new Item(c, CommentStyle.Underline));
                i++;
            }
            else if (c == '~' && next == '~')
            {
                items.Add(new Item(c, CommentStyle.Strikethrough));
                i++;
            }
            else
            {
                items.Add(new Item(c, CommentStyle.None));
            }
        }

        return items;
    }

    /// <summary>
    /// Decides which markers count: one opens when text that is not blank follows it, closes the open one of its style when text that is not
    /// blank comes before it and something besides blank space was styled. A marker that finds no partner stays text.
    /// </summary>
    private static Role[] Pair(List<Item> items)
    {
        var roles = new Role[items.Count];
        var opened = Enumerable.Repeat(-1, Styles.Length).ToArray();

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Marker == CommentStyle.None) continue;

            var slot = Array.IndexOf(Styles, items[i].Marker);
            if (opened[slot] >= 0)
            {
                if (CanClose(items, opened[slot], i))
                {
                    roles[opened[slot]] = Role.Opens;
                    roles[i] = Role.Closes;
                    opened[slot] = -1;
                }
            }
            else if (i + 1 < items.Count && !IsBlank(items[i + 1]))
            {
                opened[slot] = i;
            }
        }

        return roles;
    }

    private static bool CanClose(List<Item> items, int opener, int closer) =>
        closer > 0 && !IsBlank(items[closer - 1]) && items.Skip(opener + 1).Take(closer - opener - 1).Any(item => item.Marker == CommentStyle.None && !char.IsWhiteSpace(item.Char));

    private static bool IsBlank(Item item) => item.Marker == CommentStyle.None && char.IsWhiteSpace(item.Char);
}
