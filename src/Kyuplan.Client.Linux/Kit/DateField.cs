using System.ComponentModel;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>A date chosen from a calendar in a pop-over, bound to a <c>DateTime?</c> property (<c>null</c>: no date). Clearing is a button next to it.</summary>
internal static class DateField
{
    /// <param name="emptyText">What the button says while there is no date ("not known").</param>
    public static Gtk.Widget Create(INotifyPropertyChanged source, string property, string emptyText, string name)
    {
        var label = Ui.Label("");
        label.Wrap = false;
        var button = Gtk.MenuButton.New();
        button.SetChild(Ui.HBox(8, Gtk.Image.NewFromIconName("x-office-calendar-symbolic"), label.Expand()));
        button.Hexpand = true;
        button.Named(name);

        var calendar = Gtk.Calendar.New();
        var popover = Gtk.Popover.New();
        popover.SetChild(calendar.Margin(6));
        button.SetPopover(popover);
        var updating = false;

        DateTime? Current() => Bind.GetRaw(source, property) as DateTime?;

        void Refresh()
        {
            updating = true;
            var date = Current();
            label.SetText(date is { } value ? Display.Date(DateOnly.FromDateTime(value)) : emptyText);
            if (date is { } day)
            {
                calendar.Year = day.Year;
                calendar.Month = day.Month - 1;
                calendar.Day = day.Day;
            }

            updating = false;
        }

        calendar.OnDaySelected += (_, _) =>
        {
            if (updating) return;

            Bind.Set(source, property, new DateTime(calendar.Year, calendar.Month + 1, calendar.Day));
            popover.Popdown();
        };
        Bind.Watch(button, source, Refresh, property);
        return button;
    }

    /// <summary>The date field with a button that empties it.</summary>
    public static Gtk.Widget WithClear(INotifyPropertyChanged source, string property, string emptyText, string name, System.Windows.Input.ICommand clear, string clearLabel) =>
        Ui.HBox(6, Create(source, property, emptyText, name), Ui.Button(clearLabel, clear));
}
