using System.ComponentModel;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>A column of a <see cref="Table"/>: its heading, the cell of a row, and the width it asks for (0: takes what is left).</summary>
internal sealed record Column<T>(string Heading, Func<T, Gtk.Widget> Cell, bool Expand = false, string? Tooltip = null, Action? OnHeading = null);

/// <summary>
/// A table of rows, drawn as a grid: a heading line and one line per element, with the cells of a column aligned. Rebuilt whenever the
/// view model's <c>properties</c> change. (Plain widgets; the tables of this program have tens to a few hundred rows.)
/// </summary>
internal static class Table
{
    public static Gtk.Widget Create<T>(INotifyPropertyChanged source, Func<IEnumerable<T>> rows, string[] properties, params Column<T>[] columns)
    {
        var grid = Gtk.Grid.New();
        grid.ColumnSpacing = 14;
        grid.RowSpacing = 4; // cells with a border of their own (drop-downs, entries, buttons) would touch otherwise
        grid.AddCssClass("table");

        void Rebuild()
        {
            while (grid.GetFirstChild() is { } child) grid.Remove(child);

            for (var c = 0; c < columns.Length; c++)
            {
                var column = columns[c];
                Gtk.Widget head;
                if (column.OnHeading is { } click)
                {
                    var button = Ui.Button(column.Heading, click, "flat");
                    ((Gtk.Label)button.GetChild()!).Xalign = 0;
                    head = button;
                }
                else
                {
                    head = Ui.Label(column.Heading, "caption-heading", "dim-label").Start();
                    ((Gtk.Label)head).Wrap = false;
                }

                head.Tip(column.Tooltip);
                head.Hexpand = column.Expand;
                head.Halign = Gtk.Align.Fill;
                head.AddCssClass("table-head");
                grid.Attach(head, c, 0, 1, 1);
            }

            var r = 1;
            foreach (var row in rows())
            {
                for (var c = 0; c < columns.Length; c++)
                {
                    var cell = columns[c].Cell(row);
                    cell.Hexpand = columns[c].Expand;
                    cell.Valign = Gtk.Align.Center;
                    cell.AddCssClass("table-cell");
                    grid.Attach(cell, c, r, 1, 1);
                }

                r++;
            }
        }

        Bind.Watch(grid, source, Rebuild, properties);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(grid);
        scroll.HscrollbarPolicy = Gtk.PolicyType.Automatic;
        scroll.VscrollbarPolicy = Gtk.PolicyType.Never;
        scroll.PropagateNaturalHeight = true;
        return scroll;
    }

    /// <summary>A cell with text that is cut off with an ellipsis when it does not fit.</summary>
    public static Gtk.Label Text(string? text, params string[] css)
    {
        var label = Ui.Label(text, css);
        label.Wrap = false;
        label.Ellipsize = Pango.EllipsizeMode.End;
        label.Xalign = 0;
        return label;
    }
}
