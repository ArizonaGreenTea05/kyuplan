using System.ComponentModel;
using System.Windows.Input;
using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages.Parts;

/// <summary>Picks any number of the subgroups of the selected group (nothing ticked means everyone): a button that says what is chosen, with the list in a pop-over.</summary>
internal static class SubGroupPicker
{
    public static Gtk.Widget Create(INotifyPropertyChanged source, Func<IReadOnlyList<SubGroupOption>> options, Func<string> summary, string[] properties, ICommand clear, string name)
    {
        var label = Ui.Label("");
        label.Wrap = false;
        label.Ellipsize = Pango.EllipsizeMode.End;
        var button = Gtk.MenuButton.New();
        button.SetChild(Ui.HBox(8, label.Expand(), Gtk.Image.NewFromIconName("pan-down-symbolic")));
        button.Hexpand = true;
        button.Named(name);

        var list = Ui.VBox(2);
        var popover = Gtk.Popover.New();
        popover.SetChild(Ui.VBox(6, list, Ui.Button(Strings.Stats_Everyone, clear)).Padding(8));
        button.SetPopover(popover);

        Bind.Watch(button, source, () =>
        {
            label.SetText(summary());
            while (list.GetFirstChild() is { } child) list.Remove(child);
            foreach (var option in options()) list.Append(Ui.Check(option, nameof(SubGroupOption.IsSelected), option.Name));
        }, properties);
        return button;
    }
}
