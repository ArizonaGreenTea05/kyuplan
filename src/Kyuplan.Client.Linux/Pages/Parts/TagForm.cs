using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages.Parts;

/// <summary>The form that creates or changes a tag: its name and the event it belongs to (<see cref="TagEditorViewModel"/>). Used on the tags page and in the comments.</summary>
internal static class TagForm
{
    public static Gtk.Widget Build(TagEditorViewModel editor)
    {
        var name = Ui.Entry(editor, nameof(TagEditorViewModel.Name), name: Strings.Tags_FormName);
        name.OnActivate += (_, _) => editor.SaveCommand.Execute(null);

        var events = EventPicker.Create(editor, () => editor.EventOptions, () => editor.EventId, id => editor.EventId = id,
            [nameof(TagEditorViewModel.EventOptions), nameof(TagEditorViewModel.EventId)], Strings.Tags_FormEvent);

        var form = Ui.VBox(10,
            Ui.Label(editor, nameof(TagEditorViewModel.Title), "heading").Start(),
            Ui.Field(Strings.Tags_FormName, name),
            Ui.Label(editor, () => editor.NameProblem, [nameof(TagEditorViewModel.NameProblem), nameof(TagEditorViewModel.Name)], "error")
                .VisibleWhen(editor, () => editor.NameProblem is not null, nameof(TagEditorViewModel.NameProblem), nameof(TagEditorViewModel.Name)),
            Ui.Field(Strings.Tags_FormEvent, Ui.HBox(8, events, Ui.Button(Strings.Tags_ClearEvent, editor.ClearEventCommand)), Strings.Tags_FormEventHint),
            Ui.ErrorLine(editor),
            Ui.HBox(8,
                Ui.Primary(editor.IsCreating ? Strings.Tags_Create : Strings.Tags_Save, editor.SaveCommand),
                Ui.Button(Strings.Tags_Cancel, editor.CancelCommand)));
        return form;
    }

    /// <summary>The form, shown while the editor is open, with the right label on the save button.</summary>
    public static Gtk.Widget Panel(TagEditorViewModel editor)
    {
        var holder = Ui.VBox(0);
        Bind.Watch(holder, editor, () =>
        {
            while (holder.GetFirstChild() is { } child) holder.Remove(child);
            if (editor.IsOpen) holder.Append(Build(editor).Padded(14));
        }, nameof(TagEditorViewModel.IsOpen), nameof(TagEditorViewModel.Editing));
        return holder.Css("card").VisibleWhen(editor, nameof(TagEditorViewModel.IsOpen));
    }
}
