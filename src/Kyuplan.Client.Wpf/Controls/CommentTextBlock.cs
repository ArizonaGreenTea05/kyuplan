using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// A comment's words as the styled stretches <see cref="CommentFormat"/> made of them: bold, italic, underlined, crossed out. Only text is
/// ever put on the screen (a <see cref="Run"/> per stretch, a <see cref="LineBreak"/> for every new line), so nothing a user typed is
/// taken for anything else. Binding <see cref="Spans"/> is all it takes.
/// </summary>
public sealed class CommentTextBlock : TextBlock
{
    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(IEnumerable<CommentSpan>), typeof(CommentTextBlock),
        new PropertyMetadata(null, (d, _) => ((CommentTextBlock)d).Rebuild()));

    public CommentTextBlock() => TextWrapping = TextWrapping.Wrap;

    public IEnumerable<CommentSpan>? Spans
    {
        get => (IEnumerable<CommentSpan>?)GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();

        foreach (var span in Spans ?? [])
        {
            var lines = span.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) Inlines.Add(new LineBreak());
                if (lines[i].Length > 0) Inlines.Add(Styled(lines[i], span.Style));
            }
        }
    }

    private static Run Styled(string text, CommentStyle style)
    {
        var run = new Run(text);
        if (style.HasFlag(CommentStyle.Bold)) run.FontWeight = FontWeights.Bold;
        if (style.HasFlag(CommentStyle.Italic)) run.FontStyle = FontStyles.Italic;

        var decorations = new TextDecorationCollection();
        if (style.HasFlag(CommentStyle.Underline)) foreach (var decoration in System.Windows.TextDecorations.Underline) decorations.Add(decoration);
        if (style.HasFlag(CommentStyle.Strikethrough)) foreach (var decoration in System.Windows.TextDecorations.Strikethrough) decorations.Add(decoration);
        if (decorations.Count > 0) run.TextDecorations = decorations;

        return run;
    }
}
