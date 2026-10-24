using System.ComponentModel;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>
/// Picks an event out of a long list: a button that shows the choice and opens a list with a search field above it (by name or by date, as people
/// write it: "prüfung sep", "26.9.", see <see cref="EventOption.Matches"/>).
/// </summary>
internal static class EventPicker
{
    public static Gtk.Widget Create(INotifyPropertyChanged source, Func<IReadOnlyList<EventOption>> options, Func<string> selectedId, Action<string> select,
        string[] properties, string name)
    {
        var button = Gtk.MenuButton.New();
        button.Hexpand = true;
        var label = Ui.Label("");
        label.Wrap = false;
        label.Ellipsize = Pango.EllipsizeMode.End;
        button.SetChild(Ui.HBox(8, label.Expand(), Gtk.Image.NewFromIconName("pan-down-symbolic")));
        button.Named(name);

        var search = Gtk.SearchEntry.New();
        var list = Gtk.ListBox.New();
        list.SelectionMode = Gtk.SelectionMode.None;
        list.AddCssClass("navigation-sidebar");
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(list);
        scroll.MinContentHeight = 280;
        scroll.MinContentWidth = 420;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        var popover = Gtk.Popover.New();
        popover.SetChild(Ui.VBox(6, search, scroll).Padding(8));
        button.SetPopover(popover);

        void Fill()
        {
            list.RemoveAll();
            foreach (var option in options().Where(o => o.Matches(search.GetText())))
            {
                var row = Gtk.Button.New();
                row.AddCssClass("flat");
                var text = Ui.Label(option.Label);
                text.Wrap = false;
                row.SetChild(text);
                row.OnClicked += (_, _) =>
                {
                    select(option.Id);
                    popover.Popdown();
                };
                list.Append(row);
            }
        }

        void Refresh()
        {
            var id = selectedId();
            label.SetText(options().FirstOrDefault(o => o.Id == id)?.Label ?? options().FirstOrDefault()?.Label ?? "");
            Fill();
        }

        search.OnSearchChanged += (_, _) => Fill();
        popover.OnShow += (_, _) =>
        {
            search.SetText("");
            Fill();
            search.GrabFocus();
        };
        Bind.Watch(button, source, Refresh, properties);
        return button;
    }
}
