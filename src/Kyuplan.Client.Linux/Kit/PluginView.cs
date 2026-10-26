using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>
/// Draws what a plugin describes in JSON (<see cref="PluginItemViewModel"/>: headings, texts, fields, lists, tables, buttons, forms): one control per kind of element,
/// the same for every plugin. Used for the pages of plugins and for the settings form of a plugin's data source.
/// </summary>
internal static class PluginView
{
    public static Gtk.Widget Build(IReadOnlyList<PluginItemViewModel> items) => Ui.VBox(10, [.. items.Select(Item)]);

    private static Gtk.Widget Item(PluginItemViewModel item) => item switch
    {
        PluginTextViewModel { IsHeading: true } heading => Ui.Label(heading.Text, "heading").Start(),
        PluginTextViewModel text => Ui.Muted(text.Text).Css("caption"),
        PluginFieldViewModel input => Field(input),
        PluginListViewModel list => List(list),
        PluginTableViewModel table => Table(table),
        PluginButtonViewModel button => Ui.Button(button.Label, button.Command).Start(),
        PluginFormBlockViewModel block => Ui.VBox(10,
            Build(block.Form.Items),
            Ui.Primary(block.SubmitLabel, block.Submit).Start().EnabledWhen(block.Form, () => block.Submit.CanExecute(null), nameof(PluginFormViewModel.IsValid)))
            .Css("card").Padded(12),
        _ => Ui.VBox(0),
    };

    /// <summary>One field: the control follows its input kind; what is wrong with the value is said under it.</summary>
    private static Gtk.Widget Field(PluginFieldViewModel field)
    {
        if (field.IsCheckbox)
        {
            return Ui.VBox(2,
                Ui.Check(field, nameof(PluginFieldViewModel.IsChecked), field.Label),
                Ui.Muted(field.Hint).Css("caption").VisibleWhen(field, () => field.Hint is not null, nameof(PluginFieldViewModel.Hint)));
        }

        Gtk.Widget control = field switch
        {
            { IsPassword: true } => Ui.Password(field, nameof(PluginFieldViewModel.Text), field.Label),
            { IsMultiline: true } => Ui.TextArea(field, nameof(PluginFieldViewModel.Text), 120, field.Label),
            { IsSelect: true } => Ui.Choice(field, () => field.Choices, choice => choice.Label, () => field.SelectedChoice, choice => field.SelectedChoice = choice,
                [nameof(PluginFieldViewModel.Text), nameof(PluginFieldViewModel.SelectedChoice)], field.Label),
            _ => Ui.Entry(field, nameof(PluginFieldViewModel.Text), name: field.Label),
        };

        return Ui.VBox(2,
            Ui.Field(field.Label, control, field.Hint),
            Ui.Label(field, () => field.Problem, [nameof(PluginFieldViewModel.Problem)], "error", "caption").VisibleWhen(field, () => field.Problem is not null, nameof(PluginFieldViewModel.Problem)),
            Ui.Label(field, () => field.StoredSecretHint, [nameof(PluginFieldViewModel.StoredSecretHint)], "dim-label", "caption")
                .VisibleWhen(field, () => field.StoredSecretHint is not null, nameof(PluginFieldViewModel.StoredSecretHint)));
    }

    private static Gtk.Widget List(PluginListViewModel list) =>
        Ui.VBox(8,
            Ui.Label(list.Label, "caption-heading").Start(),
            Ui.Muted(list.Hint).Css("caption").VisibleWhen(list, () => list.Hint is not null, nameof(PluginListViewModel.Hint)),
            Ui.Repeat(list, () => list.Rows, row => Row(list, row), [nameof(PluginListViewModel.Rows)], 8),
            Ui.Label(list, () => list.Problem, [nameof(PluginListViewModel.Problem)], "error", "caption").VisibleWhen(list, () => list.Problem is not null, nameof(PluginListViewModel.Problem)),
            Ui.Button(Strings.Plugin_ListAdd, list.AddRowCommand).Start());

    private static Gtk.Widget Row(PluginListViewModel list, PluginRowViewModel row) =>
        Ui.VBox(6,
            Ui.Flow([.. row.Fields.Select(Field)]),
            Ui.Button(Strings.Plugin_ListRemove, list.RemoveRowCommand, row).Start())
            .Css("card").Padded(10);

    private static Gtk.Widget Table(PluginTableViewModel table)
    {
        Gtk.Widget Cells(IEnumerable<string> cells, bool header) =>
            Ui.HBox(12, [.. cells.Select(c => Ui.Label(c, header ? "caption-heading" : "body").Expand().Tap(l => l.Xalign = 0))]).Homogeneous();

        return Ui.VBox(6,
            Ui.Label(table.Title, "heading").Start().VisibleWhen(table, () => table.Title is not null, nameof(PluginTableViewModel.Title)),
            Cells(table.Headers, header: true),
            Ui.Repeat(table, () => table.Rows, row => Cells(row, header: false), [nameof(PluginTableViewModel.Rows)], 4),
            Ui.Label(table, () => table.EmptyText, [nameof(PluginTableViewModel.EmptyText)], "dim-label").VisibleWhen(table, () => table.IsEmpty, nameof(PluginTableViewModel.IsEmpty)))
            .Css("card").Padded(12);
    }
}
