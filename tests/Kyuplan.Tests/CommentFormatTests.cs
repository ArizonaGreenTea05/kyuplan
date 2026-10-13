using Kyuplan.Contracts;
using static Kyuplan.Contracts.CommentStyle;

namespace Kyuplan.Tests;

/// <summary>The markup comments are written in: what it turns into, and what the toolbar buttons do to a text.</summary>
public sealed class CommentFormatTests
{
    private static string Describe(string body) =>
        string.Join("|", CommentFormat.Parse(body).Select(s => s.Style == None ? s.Text : $"{s.Text}[{s.Style}]"));

    // --- Reading ------------------------------------------------------------------------------------------------

    [Fact]
    public void Plain_text_is_one_unstyled_stretch()
    {
        Assert.Equal("Just some words.", Describe("Just some words."));
        Assert.Empty(CommentFormat.Parse(""));
        Assert.Empty(CommentFormat.Parse(null));
    }

    [Theory]
    [InlineData("a **bold** word", "a |bold[Bold]| word")]
    [InlineData("a *slanted* word", "a |slanted[Italic]| word")]
    [InlineData("a __marked__ word", "a |marked[Underline]| word")]
    [InlineData("a ~~wrong~~ word", "a |wrong[Strikethrough]| word")]
    public void Each_style_covers_the_words_between_its_markers(string body, string expected) =>
        Assert.Equal(expected, Describe(body));

    [Fact]
    public void Styles_combine_and_may_cross()
    {
        Assert.Equal("both[Bold, Italic]", Describe("***both***"));
        Assert.Equal("a[Bold]|b[Bold, Italic]|c[Italic]", Describe("**a*b**c*"));
        Assert.Equal("all[Bold, Italic, Underline, Strikethrough]", Describe("**__*~~all~~*__**"));
    }

    [Fact]
    public void A_style_may_run_over_several_lines()
    {
        var span = Assert.Single(CommentFormat.Parse("**one\ntwo**"));

        Assert.Equal(("one\ntwo", Bold), (span.Text, span.Style));
    }

    [Theory]
    [InlineData("5 * 3 * 2")]            // a marker next to blank space is not one
    [InlineData("snake_case_name")]       // single underscores and tildes are just characters
    [InlineData("about ~5 km")]
    [InlineData("**unfinished")]          // no partner: stays as it was typed
    [InlineData("a ** b ** c")]
    [InlineData("****")]                  // nothing in between
    public void What_is_not_a_style_stays_text(string body) =>
        Assert.Equal(body, Describe(body));

    [Fact]
    public void A_backslash_writes_a_marker_character_itself()
    {
        Assert.Equal(@"2 * 3 and *4*", CommentFormat.ToPlainText(@"2 \* 3 and \*4\*"));
        Assert.Equal(@"a\b", CommentFormat.ToPlainText(@"a\b"));      // not in front of a marker character: an ordinary backslash
        Assert.Equal(@"\", CommentFormat.ToPlainText(@"\\"));
        Assert.Equal("*not italic*", Describe(@"\*not italic\*"));
    }

    [Fact]
    public void Plain_text_is_the_words_without_the_markers()
    {
        Assert.Equal("a bold and slanted word", CommentFormat.ToPlainText("a **bold** and *slanted* word"));
        Assert.Equal("", CommentFormat.ToPlainText(null));
    }

    [Fact]
    public void A_body_is_cleaned_to_what_is_stored()
    {
        Assert.Equal("one\ntwo\nthree", CommentFormat.Clean("  one\r\ntwo\rthree \n\n"));
        Assert.Equal("", CommentFormat.Clean(null));
    }

    // --- Comparing ------------------------------------------------------------------------------------------------

    [Fact]
    public void Comparing_tells_a_change_of_the_words_from_a_change_of_the_styling()
    {
        Assert.Equal((false, false), CommentFormat.Compare("same", "same"));
        Assert.Equal((true, false), CommentFormat.Compare("old words", "new words"));
        Assert.Equal((false, true), CommentFormat.Compare("old words", "**old** words"));
        Assert.Equal((true, false), CommentFormat.Compare("**old** words", "**new** words")); // words win: the styling is not compared on top
    }

    // --- The toolbar ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_selection_is_wrapped_in_the_markers_and_stays_selected()
    {
        var change = CommentFormat.Toggle("a word here", 2, 4, Bold);

        Assert.Equal(new TextChange("a **word** here", 4, 4), change);
        Assert.Equal("word", change.Text.Substring(change.SelectionStart, change.SelectionLength));
    }

    [Fact]
    public void Nothing_selected_inserts_a_pair_of_markers_with_the_cursor_between()
    {
        Assert.Equal(new TextChange("ab____cd", 4, 0), CommentFormat.Toggle("abcd", 2, 0, Underline));
    }

    [Fact]
    public void Blank_space_at_the_ends_of_a_selection_stays_outside_the_markers()
    {
        // A double click on a word often takes the space behind it along; "**word **" would not be bold.
        var change = CommentFormat.Toggle("a word here", 2, 5, Italic);

        Assert.Equal(new TextChange("a *word* here", 3, 4), change);
        Assert.Equal("word", CommentFormat.Parse(change.Text)[1].Text);
    }

    [Theory]
    [InlineData(Bold, "**", "**")]
    [InlineData(Italic, "*", "*")]
    [InlineData(Underline, "__", "__")]
    [InlineData(Strikethrough, "~~", "~~")]
    public void Pressing_a_button_again_takes_the_style_off(CommentStyle style, string open, string close)
    {
        var on = CommentFormat.Toggle("a word here", 2, 4, style);
        Assert.Contains(open + "word" + close, on.Text);

        var off = CommentFormat.Toggle(on.Text, on.SelectionStart, on.SelectionLength, style);

        Assert.Equal(new TextChange("a word here", 2, 4), off);
    }

    [Fact]
    public void A_selection_that_includes_the_markers_takes_the_style_off_too()
    {
        // Selected "**word**" itself, not just word.
        Assert.Equal(new TextChange("a word here", 2, 4), CommentFormat.Toggle("a **word** here", 2, 8, Bold));
    }

    [Fact]
    public void Italic_is_not_taken_for_a_part_of_bold_and_the_other_way_round()
    {
        var bold = CommentFormat.Toggle("a word", 2, 4, Bold);      // a **word**

        var both = CommentFormat.Toggle(bold.Text, bold.SelectionStart, bold.SelectionLength, Italic);
        Assert.Equal("a ***word***", both.Text);
        Assert.Equal("a |word[Bold, Italic]", Describe(both.Text));

        var onlyBold = CommentFormat.Toggle(both.Text, both.SelectionStart, both.SelectionLength, Italic);
        Assert.Equal("a **word**", onlyBold.Text);

        var onlyItalic = CommentFormat.Toggle(both.Text, both.SelectionStart, both.SelectionLength, Bold);
        Assert.Equal("a *word*", onlyItalic.Text);
    }

    [Fact]
    public void The_same_button_twice_without_a_selection_leaves_the_text_as_it_was()
    {
        var first = CommentFormat.Toggle("ab", 1, 0, Strikethrough);
        var second = CommentFormat.Toggle(first.Text, first.SelectionStart, first.SelectionLength, Strikethrough);

        Assert.Equal(new TextChange("ab", 1, 0), second);
    }

    [Fact]
    public void A_selection_outside_the_text_is_pulled_in()
    {
        Assert.Equal(new TextChange("ab****", 4, 0), CommentFormat.Toggle("ab", 40, 9, Bold));
        Assert.Equal(new TextChange("**ab**", 2, 2), CommentFormat.Toggle("ab", -3, 40, Bold));
    }
}
