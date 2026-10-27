using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>Small factories for the widgets the pages are made of, so that a page reads like its XAML twin in the WPF client.</summary>
internal static class Ui
{
    // ---- layout ------------------------------------------------------------------------------------------------------------

    public static Gtk.Box VBox(int spacing = 12, params Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, spacing);
        foreach (var child in children) box.Append(child);
        return box;
    }

    public static Gtk.Box HBox(int spacing = 8, params Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, spacing);
        foreach (var child in children) box.Append(child);
        return box;
    }

    /// <summary>Children that wrap onto the next line when the window is narrow (filter bars, chips).</summary>
    public static Gtk.FlowBox Flow(params Gtk.Widget[] children)
    {
        var flow = Gtk.FlowBox.New();
        flow.SelectionMode = Gtk.SelectionMode.None;
        flow.ColumnSpacing = 8;
        flow.RowSpacing = 8;
        flow.MaxChildrenPerLine = 12;
        foreach (var child in children) flow.Append(child);
        return flow;
    }

    /// <summary>Changes a widget and gives it back, so that a cell can be set up in one expression.</summary>
    public static T Tap<T>(this T widget, Action<T> change)
    {
        change(widget);
        return widget;
    }

    public static Gtk.Box Homogeneous(this Gtk.Box box)
    {
        box.Homogeneous = true;
        return box;
    }

    public static T Margin<T>(this T widget, int all) where T : Gtk.Widget => widget.Margin(all, all);

    public static T Margin<T>(this T widget, int vertical, int horizontal) where T : Gtk.Widget
    {
        widget.MarginTop = widget.MarginBottom = vertical;
        widget.MarginStart = widget.MarginEnd = horizontal;
        return widget;
    }

    public static T Css<T>(this T widget, params string[] classes) where T : Gtk.Widget
    {
        foreach (var name in classes)
        {
            if (name.Length > 0) widget.AddCssClass(name); // "" (a condition that adds no class) is a GTK critical otherwise
        }

        return widget;
    }

    public static T Expand<T>(this T widget, bool horizontal = true, bool vertical = false) where T : Gtk.Widget
    {
        widget.Hexpand = horizontal;
        widget.Vexpand = vertical;
        return widget;
    }

    public static T Start<T>(this T widget) where T : Gtk.Widget
    {
        widget.Halign = Gtk.Align.Start;
        return widget;
    }

    public static T End<T>(this T widget) where T : Gtk.Widget
    {
        widget.Halign = Gtk.Align.End;
        return widget;
    }

    public static T Tip<T>(this T widget, string? text) where T : Gtk.Widget
    {
        widget.TooltipText = text;
        return widget;
    }

    /// <summary>
    /// Names a control that has no visible label of its own (an icon button, a search field): GTK uses the tooltip as the accessible
    /// name then. A control with a label is named by that label (<see cref="Field"/> ties them together).
    /// </summary>
    public static T Named<T>(this T widget, string name) where T : Gtk.Widget
    {
        widget.TooltipText ??= name;
        return widget;
    }

    // ---- text --------------------------------------------------------------------------------------------------------------

    public static Gtk.Label Label(string? text, params string[] css)
    {
        var label = Gtk.Label.New(text ?? "");
        label.Xalign = 0;
        label.Wrap = true;
        label.WrapMode = Pango.WrapMode.WordChar;
        return label.Css(css);
    }

    /// <summary>A short label that never wraps (a caption next to a control).</summary>
    public static Gtk.Label Caption(string text, params string[] css)
    {
        var label = Label(text, css);
        label.Wrap = false;
        return label;
    }

    public static Gtk.Label Heading(string text) => Label(text, "title-2").Start();

    public static Gtk.Label Muted(string? text) => Label(text, "dim-label");

    /// <summary>A label that shows what <paramref name="text"/> returns, again whenever one of the view model's <paramref name="properties"/> changes.</summary>
    public static Gtk.Label Label(INotifyPropertyChanged source, Func<string?> text, string[] properties, params string[] css)
    {
        var label = Label("", css);
        Bind.Watch(label, source, () => label.SetText(text() ?? ""), properties);
        return label;
    }

    /// <summary>A label bound to one property (its text; null and empty show nothing).</summary>
    public static Gtk.Label Label(INotifyPropertyChanged source, string property, params string[] css) =>
        Label(source, () => Bind.GetRaw(source, property)?.ToString(), [property], css);

    /// <summary>Shows <paramref name="widget"/> only while <paramref name="visible"/> is true.</summary>
    public static T VisibleWhen<T>(this T widget, INotifyPropertyChanged source, Func<bool> visible, params string[] properties) where T : Gtk.Widget
    {
        Bind.Watch(widget, source, () => widget.Visible = visible(), properties);
        return widget;
    }

    public static T VisibleWhen<T>(this T widget, INotifyPropertyChanged source, string property) where T : Gtk.Widget =>
        widget.VisibleWhen(source, () => Bind.Get<bool>(source, property), property);

    public static T HiddenWhen<T>(this T widget, INotifyPropertyChanged source, string property) where T : Gtk.Widget =>
        widget.VisibleWhen(source, () => !Bind.Get<bool>(source, property), property);

    public static T EnabledWhen<T>(this T widget, INotifyPropertyChanged source, Func<bool> enabled, params string[] properties) where T : Gtk.Widget
    {
        Bind.Watch(widget, source, () => widget.Sensitive = enabled(), properties);
        return widget;
    }

    public static T EnabledWhen<T>(this T widget, INotifyPropertyChanged source, string property) where T : Gtk.Widget =>
        widget.EnabledWhen(source, () => Bind.Get<bool>(source, property), property);

    // ---- buttons -----------------------------------------------------------------------------------------------------------

    public static Gtk.Button Button(string label, Action onClick, params string[] css)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.OnClicked += (_, _) => onClick();
        return button.Css(css);
    }

    /// <summary>A button that runs a view model command and is disabled while the command cannot run.</summary>
    public static Gtk.Button Button(string label, ICommand command, object? parameter = null, params string[] css)
    {
        var button = Button(label, () => command.Execute(parameter), css);
        FollowCanExecute(button, command, parameter);
        return button;
    }

    public static Gtk.Button Primary(string label, ICommand command, object? parameter = null) => Button(label, command, parameter, "suggested-action");

    public static Gtk.Button Danger(string label, ICommand command, object? parameter = null) => Button(label, command, parameter, "destructive-action");

    public static Gtk.Button IconButton(string icon, string tooltip, Action onClick, params string[] css)
    {
        var button = Gtk.Button.NewFromIconName(icon);
        button.OnClicked += (_, _) => onClick();
        return button.Tip(tooltip).Named(tooltip).Css(css);
    }

    public static Gtk.Button IconButton(string icon, string tooltip, ICommand command, object? parameter = null, params string[] css)
    {
        var button = IconButton(icon, tooltip, () => command.Execute(parameter), css);
        FollowCanExecute(button, command, parameter);
        return button;
    }

    private static void FollowCanExecute(Gtk.Widget button, ICommand command, object? parameter)
    {
        void Refresh() => button.Sensitive = command.CanExecute(parameter);
        void Handler(object? sender, EventArgs e) => Refresh();
        command.CanExecuteChanged += Handler;
        button.OnDestroy += (_, _) => command.CanExecuteChanged -= Handler;
        Refresh();
    }

    /// <summary>A button without a frame, for secondary actions.</summary>
    public static Gtk.Button FlatButton(string label, Action onClick) => Button(label, onClick, "flat");

    // ---- inputs ------------------------------------------------------------------------------------------------------------

    /// <summary>A text field bound both ways to a string property.</summary>
    public static Gtk.Entry Entry(INotifyPropertyChanged source, string property, string? placeholder = null, string? name = null)
    {
        var entry = Gtk.Entry.New();
        entry.PlaceholderText = placeholder;
        entry.Hexpand = true;
        Bind.Watch(entry, source, () =>
        {
            var text = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (entry.GetText() != text) entry.SetText(text);
        }, property);
        entry.OnChanged += (_, _) => Bind.Set(source, property, entry.GetText());
        return entry;
    }

    public static Gtk.PasswordEntry Password(INotifyPropertyChanged source, string property, string? name = null)
    {
        var entry = Gtk.PasswordEntry.New();
        entry.ShowPeekIcon = true;
        entry.Hexpand = true;
        Bind.Watch(entry, source, () =>
        {
            var text = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (entry.GetText() != text) entry.SetText(text);
        }, property);
        entry.OnChanged += (_, _) => Bind.Set(source, property, entry.GetText());
        return entry;
    }

    /// <summary>A search field bound to a string property.</summary>
    public static Gtk.SearchEntry Search(INotifyPropertyChanged source, string property, string? placeholder = null)
    {
        var entry = Gtk.SearchEntry.New();
        entry.PlaceholderText = placeholder;
        entry.Hexpand = true;
        Bind.Watch(entry, source, () =>
        {
            var text = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (entry.GetText() != text) entry.SetText(text);
        }, property);
        entry.OnSearchChanged += (_, _) => Bind.Set(source, property, entry.GetText());
        return entry;
    }

    public static Gtk.CheckButton Check(INotifyPropertyChanged source, string property, string label)
    {
        var check = Gtk.CheckButton.NewWithLabel(label);
        Bind.Watch(check, source, () => check.Active = Bind.Get<bool>(source, property), property);
        check.OnToggled += (_, _) => Bind.Set(source, property, check.Active);
        return check;
    }

    public static Gtk.Switch Switch(INotifyPropertyChanged source, string property)
    {
        var toggle = Gtk.Switch.New();
        toggle.Valign = Gtk.Align.Center;
        Bind.Watch(toggle, source, () => toggle.Active = Bind.Get<bool>(source, property), property);
        Bind.OnProperty(toggle, "active", () => Bind.Set(source, property, toggle.Active));
        return toggle;
    }

    /// <summary>A multi-line text field bound to a string property.</summary>
    public static Gtk.Widget TextArea(INotifyPropertyChanged source, string property, int minHeight = 96, string? name = null) =>
        TextArea(source, property, out _, minHeight, name);

    /// <summary>A multi-line text field bound to a string property; <paramref name="view"/> is the text view inside it (selection, key handling).</summary>
    public static Gtk.Widget TextArea(INotifyPropertyChanged source, string property, out Gtk.TextView view, int minHeight = 96, string? name = null)
    {
        view = Gtk.TextView.New();
        view.WrapMode = Gtk.WrapMode.WordChar;
        view.LeftMargin = view.RightMargin = 8;
        view.TopMargin = view.BottomMargin = 6;
        if (name is not null) view.Named(name);
        var buffer = view.GetBuffer();
        var textView = view;
        Bind.Watch(textView, source, () =>
        {
            var text = Text(buffer);
            var wanted = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (text != wanted) buffer.SetText(wanted, -1);
        }, property);
        buffer.OnChanged += (_, _) => Bind.Set(source, property, Text(buffer));

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(textView);
        scroll.MinContentHeight = minHeight;
        scroll.Hexpand = true;
        scroll.HasFrame = true;
        return scroll;
    }

    private static string Text(Gtk.TextBuffer buffer)
    {
        buffer.GetBounds(out var start, out var end);
        return buffer.GetText(start, end, false);
    }

    /// <summary>A number field bound to a numeric property.</summary>
    public static Gtk.SpinButton Spin(INotifyPropertyChanged source, string property, double min, double max, double step = 1, uint digits = 0, string? name = null)
    {
        var spin = Gtk.SpinButton.NewWithRange(min, max, step);
        spin.Digits = digits;
        spin.Numeric = true;
        if (name is not null) spin.Named(name);
        Bind.Watch(spin, source, () =>
        {
            var value = System.Convert.ToDouble(Bind.GetRaw(source, property) ?? 0);
            if (Math.Abs(spin.Value - value) > 1e-9) spin.Value = value;
        }, property);
        spin.OnValueChanged += (_, _) => Bind.Set(source, property, spin.Value);
        return spin;
    }

    /// <summary>
    /// A drop-down over <paramref name="items"/>. The choice is read with <paramref name="selected"/> (re-read whenever one of the
    /// <paramref name="properties"/> changes) and written with <paramref name="select"/>; the list is re-read then, too.
    /// <paramref name="leading"/> draws a picture in front of the text of an item (a belt's colours), in the button and in the list.
    /// </summary>
    public static Gtk.DropDown Choice<T>(INotifyPropertyChanged source, Func<IReadOnlyList<T>> items, Func<T, string> text, Func<T?> selected, Action<T> select,
        string[] properties, string? name = null, Func<T, Gtk.Widget>? leading = null)
    {
        var drop = Gtk.DropDown.NewFromStrings([]);
        drop.Hexpand = true;

        if (name is not null) drop.Named(name);
        IReadOnlyList<T> current = [];
        var updating = false;

        if (leading is not null)
        {
            var factory = Gtk.SignalListItemFactory.New();
            factory.OnSetup += (_, e) => ((Gtk.ListItem)e.Object).SetChild(HBox(10));
            factory.OnBind += (_, e) =>
            {
                var item = (Gtk.ListItem)e.Object;
                var line = (Gtk.Box)item.GetChild()!;
                while (line.GetFirstChild() is { } old) line.Remove(old);
                if (item.Position >= current.Count) return;

                var value = current[(int)item.Position];
                line.Append(leading(value));
                line.Append(Caption(text(value)));
            };
            // The button keeps its widget when nothing is selected any more: without this it would go on showing the last belt.
            factory.OnUnbind += (_, e) =>
            {
                var line = (Gtk.Box)((Gtk.ListItem)e.Object).GetChild()!;
                while (line.GetFirstChild() is { } old) line.Remove(old);
            };
            drop.SetFactory(factory);
        }

        void Refresh()
        {
            updating = true;
            try
            {
                var wanted = items();
                if (!wanted.SequenceEqual(current))
                {
                    current = wanted;
                    drop.SetModel(Gtk.StringList.New(wanted.Select(text).ToArray()));
                }

                var chosen = selected();
                var index = chosen is null ? -1 : wanted.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i, chosen));
                drop.Selected = index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index;
            }
            finally
            {
                updating = false;
            }
        }

        Bind.Watch(drop, source, Refresh, properties);
        Bind.OnProperty(drop, "selected", () =>
        {
            if (updating || drop.Selected >= current.Count) return;

            select(current[(int)drop.Selected]);
        });
        return drop;
    }

    // ---- lists -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows for the elements <paramref name="items"/> returns, built again whenever the view model's <paramref name="properties"/> change
    /// (or the elements change, when they are an observable collection).
    /// </summary>
    public static Gtk.Box Repeat<T>(INotifyPropertyChanged source, Func<IEnumerable<T>> items, Func<T, Gtk.Widget> row, string[] properties,
        int spacing = 8, Gtk.Orientation orientation = Gtk.Orientation.Vertical)
    {
        var box = Gtk.Box.New(orientation, spacing);
        object? watched = null;

        void Rebuild()
        {
            while (box.GetFirstChild() is { } child) box.Remove(child);
            foreach (var item in items()) box.Append(row(item));
            WatchCollection();
        }

        // A collection that is replaced announces itself through the property; one that is changed in place through CollectionChanged.
        void WatchCollection()
        {
            if (items() is not INotifyCollectionChanged observable || ReferenceEquals(observable, watched)) return;

            watched = observable;
            observable.CollectionChanged += (_, _) => Rebuild();
        }

        Bind.Watch(box, source, Rebuild, properties);
        return box;
    }

    /// <summary>A GNOME "boxed list": the rows sit in one rounded card, each can be an <see cref="Adw.ActionRow"/>.</summary>
    public static Gtk.ListBox BoxedList()
    {
        var list = Gtk.ListBox.New();
        list.SelectionMode = Gtk.SelectionMode.None;
        return list.Css("boxed-list");
    }

    public static Gtk.ListBox BoxedList<T>(INotifyPropertyChanged source, Func<IEnumerable<T>> items, Func<T, Gtk.Widget> row, string[] properties)
    {
        var list = BoxedList();
        object? watched = null;

        void Rebuild()
        {
            list.RemoveAll();
            foreach (var item in items()) list.Append(row(item));
            if (items() is INotifyCollectionChanged observable && !ReferenceEquals(observable, watched))
            {
                watched = observable;
                observable.CollectionChanged += (_, _) => Rebuild();
            }
        }

        Bind.Watch(list, source, Rebuild, properties);
        return list;
    }

    // ---- adwaita rows ------------------------------------------------------------------------------------------------------

    public static Adw.ActionRow Row(string title, string? subtitle = null, Gtk.Widget? suffix = null, Gtk.Widget? prefix = null)
    {
        var row = Adw.ActionRow.New();
        row.Title = GLib.Functions.MarkupEscapeText(title, -1);
        if (!string.IsNullOrEmpty(subtitle)) row.Subtitle = GLib.Functions.MarkupEscapeText(subtitle, -1);
        if (suffix is not null) row.AddSuffix(suffix);
        if (prefix is not null) row.AddPrefix(prefix);
        return row;
    }

    /// <summary>A row whose title and subtitle follow the view model.</summary>
    public static Adw.ActionRow Row(INotifyPropertyChanged source, Func<string?> title, Func<string?> subtitle, string[] properties, Gtk.Widget? suffix = null)
    {
        var row = Adw.ActionRow.New();
        Bind.Watch(row, source, () =>
        {
            row.Title = GLib.Functions.MarkupEscapeText(title() ?? "", -1);
            var sub = subtitle();
            row.Subtitle = string.IsNullOrEmpty(sub) ? "" : GLib.Functions.MarkupEscapeText(sub, -1);
        }, properties);
        if (suffix is not null) row.AddSuffix(suffix);
        return row;
    }

    public static Adw.EntryRow EntryRow(INotifyPropertyChanged source, string property, string title)
    {
        var row = Adw.EntryRow.New();
        row.Title = title;
        Bind.Watch(row, source, () =>
        {
            var text = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (row.GetText() != text) row.SetText(text);
        }, property);
        row.OnChanged += (_, _) => Bind.Set(source, property, row.GetText());
        return row;
    }

    public static Adw.PasswordEntryRow PasswordRow(INotifyPropertyChanged source, string property, string title)
    {
        var row = Adw.PasswordEntryRow.New();
        row.Title = title;
        Bind.Watch(row, source, () =>
        {
            var text = Bind.GetRaw(source, property)?.ToString() ?? "";
            if (row.GetText() != text) row.SetText(text);
        }, property);
        row.OnChanged += (_, _) => Bind.Set(source, property, row.GetText());
        return row;
    }

    public static Adw.SwitchRow SwitchRow(INotifyPropertyChanged source, string property, string title, string? subtitle = null)
    {
        var row = Adw.SwitchRow.New();
        row.Title = GLib.Functions.MarkupEscapeText(title, -1);
        if (!string.IsNullOrEmpty(subtitle)) row.Subtitle = GLib.Functions.MarkupEscapeText(subtitle, -1);
        Bind.Watch(row, source, () => row.Active = Bind.Get<bool>(source, property), property);
        Bind.OnProperty(row, "active", () => Bind.Set(source, property, row.Active));
        return row;
    }

    public static Adw.SpinRow SpinRow(INotifyPropertyChanged source, string property, string title, double min, double max, double step = 1, uint digits = 0)
    {
        var row = Adw.SpinRow.NewWithRange(min, max, step);
        row.Title = GLib.Functions.MarkupEscapeText(title, -1);
        row.Digits = digits;
        Bind.Watch(row, source, () =>
        {
            var value = System.Convert.ToDouble(Bind.GetRaw(source, property) ?? 0);
            if (Math.Abs(row.Value - value) > 1e-9) row.Value = value;
        }, property);
        Bind.OnProperty(row, "value", () => Bind.Set(source, property, row.Value));
        return row;
    }

    public static Adw.ComboRow ComboRow<T>(INotifyPropertyChanged source, string title, Func<IReadOnlyList<T>> items, Func<T, string> text, Func<T?> selected,
        Action<T> select, params string[] properties)
    {
        var row = Adw.ComboRow.New();
        row.Title = GLib.Functions.MarkupEscapeText(title, -1);
        IReadOnlyList<T> current = [];
        var updating = false;

        void Refresh()
        {
            updating = true;
            try
            {
                var wanted = items();
                if (!wanted.SequenceEqual(current))
                {
                    current = wanted;
                    row.SetModel(Gtk.StringList.New(wanted.Select(text).ToArray()));
                }

                var chosen = selected();
                var index = chosen is null ? -1 : wanted.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i, chosen));
                row.Selected = index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index;
            }
            finally
            {
                updating = false;
            }
        }

        Bind.Watch(row, source, Refresh, properties);
        Bind.OnProperty(row, "selected", () =>
        {
            if (updating || row.Selected >= current.Count) return;

            select(current[(int)row.Selected]);
        });
        return row;
    }

    /// <summary>A card with a title: the Adwaita preferences group.</summary>
    public static Adw.PreferencesGroup Group(string? title = null, string? description = null)
    {
        var group = Adw.PreferencesGroup.New();
        if (title is not null) group.Title = GLib.Functions.MarkupEscapeText(title, -1);
        if (description is not null) group.Description = GLib.Functions.MarkupEscapeText(description, -1);
        return group;
    }

    public static Adw.PreferencesGroup Group(string? title, params Gtk.Widget[] rows)
    {
        var group = Group(title);
        foreach (var row in rows) group.Add(row);
        return group;
    }

    /// <summary>A labelled field: caption above, widget below.</summary>
    public static Gtk.Box Field(string caption, Gtk.Widget widget, string? hint = null)
    {
        var label = Label(caption, "caption-heading").Start();
        label.SetMnemonicWidget(widget); // the label names the field for screen readers
        var box = VBox(4, label, widget);
        if (!string.IsNullOrEmpty(hint)) box.Append(Muted(hint).Css("caption"));
        return box;
    }

    /// <summary>A card (the "sheet" of the WPF client).</summary>
    public static Gtk.Box Card(params Gtk.Widget[] children)
    {
        var box = VBox(8, children);
        return box.Css("card").Padded(14);
    }

    /// <summary>Space inside a card or box (CSS padding, which stays inside the frame; <see cref="Padding{T}"/> is space outside).</summary>
    public static T Padded<T>(this T widget, int pixels) where T : Gtk.Widget => widget.Css($"pad-{pixels}");

    public static T Padding<T>(this T box, int all) where T : Gtk.Widget
    {
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = all;
        return box;
    }

    /// <summary>A tag or status: small text with a background (the CSS classes come from <see cref="Theming"/>).</summary>
    public static Gtk.Label Chip(string text, params string[] css)
    {
        var label = Gtk.Label.New(text);
        label.Css(css).Css("caption", "chip");
        return label;
    }

    /// <summary>A line that tells the user what went wrong: shown while the view model has an error.</summary>
    public static Gtk.Widget ErrorLine(INotifyPropertyChanged source, string textProperty = "ErrorMessage")
    {
        var label = Label(source, textProperty, "error");
        label.VisibleWhen(source, () => !string.IsNullOrEmpty(Bind.GetRaw(source, textProperty) as string), textProperty);
        return label;
    }

    /// <summary>A spinner that turns while the view model is busy.</summary>
    public static Gtk.Spinner BusySpinner(INotifyPropertyChanged source)
    {
        var spinner = Gtk.Spinner.New();
        Bind.Watch(spinner, source, () =>
        {
            var busy = Bind.Get<bool>(source, "IsBusy");
            spinner.Spinning = busy;
            spinner.Visible = busy;
        }, "IsBusy");
        return spinner;
    }

    /// <summary>A scrolling page: a column of at most <paramref name="maxWidth"/> pixels in the middle of the window.</summary>
    public static Gtk.ScrolledWindow Page(Gtk.Widget content, int maxWidth = 1100)
    {
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = maxWidth;
        clamp.TighteningThreshold = maxWidth - 200;
        clamp.SetChild(content.Margin(18, 24));
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(clamp);
        scroll.Hexpand = scroll.Vexpand = true;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        return scroll;
    }
}
