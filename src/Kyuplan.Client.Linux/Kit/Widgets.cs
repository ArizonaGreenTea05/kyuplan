using System.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>The building blocks the pages share: page header, key-figure tiles, bars and charts, and the "not allowed" / "loading" frame every page sits in.</summary>
internal static class Widgets
{
    /// <summary>
    /// The frame of a page: the loading bar on top, the page when the user may use it (else a note), scrolling. <paramref name="body"/> is
    /// the content under the header.
    /// </summary>
    public static Gtk.Widget PageFrame(PageViewModel page, Gtk.Widget body, int maxWidth = 1100)
    {
        var bar = Gtk.ProgressBar.New();
        bar.AddCssClass("osd");
        bar.Valign = Gtk.Align.Start;
        bar.Visible = false;
        bar.CanTarget = false;
        var pulsing = false;
        Bind.Watch(bar, page, () =>
        {
            bar.Visible = page.IsBusy;
            if (!page.IsBusy || pulsing) return;

            pulsing = true;
            GLib.Functions.TimeoutAdd(0, 120, () =>
            {
                if (!page.IsBusy)
                {
                    pulsing = false;
                    return false;
                }

                bar.Pulse();
                return true;
            });
        }, nameof(PageViewModel.IsBusy));

        var allowed = Ui.Page(body, maxWidth).VisibleWhen(page, nameof(PageViewModel.CanView));
        var denied = Adw.StatusPage.New();
        denied.IconName = "action-unavailable-symbolic";
        denied.Title = Strings.Page_NotAllowed;
        denied.VisibleWhen(page, () => !page.CanView);

        var overlay = Gtk.Overlay.New();
        overlay.SetChild(Ui.VBox(0, allowed, denied));
        overlay.AddOverlay(bar);
        return overlay;
    }

    /// <summary>The title of a page with a line under it, and a control on the right (a picker, buttons).</summary>
    public static Gtk.Widget Header(string title, Gtk.Widget? lede = null, Gtk.Widget? trailing = null)
    {
        var text = Ui.VBox(2, Ui.Label(title, "title-1").Start());
        if (lede is not null) text.Append(lede);
        var row = Ui.HBox(12, text.Expand());
        if (trailing is not null)
        {
            trailing.Valign = Gtk.Align.End;
            row.Append(trailing);
        }

        return row;
    }

    public static Gtk.Widget Lede(INotifyPropertyChanged source, Func<string?> text, params string[] properties) =>
        Ui.Label(source, text, properties, "dim-label");

    /// <summary>A problem the page ran into, with a button to try again.</summary>
    public static Gtk.Widget ErrorBanner(ViewModelBase model, System.Windows.Input.ICommand? retry = null)
    {
        var row = Ui.HBox(12, Ui.Label(model, nameof(ViewModelBase.ErrorMessage)).Expand());
        if (retry is not null) row.Append(Ui.Button(Strings.Shell_TryAgain, retry));
        return row.Css("banner-problem").VisibleWhen(model, nameof(ViewModelBase.HasError));
    }

    /// <summary>"Are you sure?" under the page's header: shown while <paramref name="visibleProperty"/> is true; nothing is deleted before it is answered.</summary>
    public static Gtk.Widget ConfirmBar(INotifyPropertyChanged model, string visibleProperty, string textProperty, System.Windows.Input.ICommand yes, string yesLabel,
        System.Windows.Input.ICommand no, string noLabel) =>
        Ui.HBox(12, Ui.Label(model, textProperty).Expand(), Ui.Button(yesLabel, yes, null, "destructive-action"), Ui.Button(noLabel, no))
            .Css("banner-problem").VisibleWhen(model, visibleProperty);

    /// <summary>A line that says what has just been done ("Saved"); shown while the view model has one.</summary>
    public static Gtk.Widget Notice(INotifyPropertyChanged model) =>
        Ui.Label(model, "NoticeText", "accent").VisibleWhen(model, "HasNotice");

    /// <summary>A titled card (the "sheet" of the WPF client): a heading and what goes in it.</summary>
    public static Gtk.Box Section(string title, params Gtk.Widget[] content)
    {
        var box = Ui.VBox(10, Ui.Label(title, "heading").Start());
        foreach (var child in content) box.Append(child);
        return box.Css("card").Padded(16);
    }

    /// <summary>A tile with one key figure.</summary>
    public static Gtk.Widget Kpi(KpiViewModel kpi, bool compact = false)
    {
        var value = Ui.Label(kpi.Value, compact ? "title-2" : "title-1").Start();
        if (kpi.Tone == KpiTone.Warning) value.AddCssClass("warn-text");
        else if (kpi.Tone == KpiTone.Success) value.AddCssClass("good-text");

        var tile = Ui.VBox(2, Ui.Label(kpi.Label, "caption-heading", "dim-label").Start(), value);
        if (!compact && !string.IsNullOrEmpty(kpi.Hint)) tile.Append(Ui.Muted(kpi.Hint).Css("caption"));
        tile.Tip(kpi.Hint);
        return tile.Css("card").Padded(compact ? 10 : 14);
    }

    /// <summary>The key figures in a grid that wraps with the window.</summary>
    public static Gtk.Widget KpiGrid(IEnumerable<KpiViewModel> kpis, bool compact = false)
    {
        var flow = Gtk.FlowBox.New();
        flow.SelectionMode = Gtk.SelectionMode.None;
        flow.Homogeneous = true;
        flow.ColumnSpacing = flow.RowSpacing = 12;
        flow.MinChildrenPerLine = 2;
        flow.MaxChildrenPerLine = 6;
        foreach (var kpi in kpis) flow.Append(Kpi(kpi, compact));
        return flow;
    }

    /// <summary>A bar that is as full as <paramref name="fraction"/> (0–1). The style is <c>warn</c> / <c>bad</c> for the amber / red fill.</summary>
    public static Gtk.ProgressBar RateBar(double fraction, string? style = null, string? description = null)
    {
        var bar = Gtk.ProgressBar.New();
        bar.Fraction = Math.Clamp(fraction, 0, 1);
        bar.Valign = Gtk.Align.Center;
        bar.Hexpand = true;
        bar.AddCssClass("rate-bar");
        if (style is not null) bar.AddCssClass(style);
        return bar.Tip(description);
    }

    /// <summary>A bar of three shares that add up to a whole: yes, no and still open (three bars in one place, each on top of the one for the larger share).</summary>
    public static Gtk.Widget StackedBar(double yes, double no, double open, string? description = null)
    {
        Gtk.ProgressBar Layer(double fraction, string css)
        {
            var bar = Gtk.ProgressBar.New();
            bar.Fraction = Math.Clamp(fraction, 0, 1);
            bar.Hexpand = true;
            bar.AddCssClass("stacked-bar");
            bar.AddCssClass(css);
            return bar;
        }

        var overlay = Gtk.Overlay.New();
        overlay.SetChild(Layer(yes + no + open, "bar-open"));
        overlay.AddOverlay(Layer(yes + no, "bar-no"));
        overlay.AddOverlay(Layer(yes, "bar-yes"));
        overlay.Hexpand = true;
        return overlay.Tip(description);
    }

    /// <summary>A column chart from simple bars: the value above, the label below; the highlighted bar in the accent colour.</summary>
    public static Gtk.Widget ColumnChart(IReadOnlyList<BarViewModel> bars, int height = 150)
    {
        var row = Ui.HBox(6);
        row.Homogeneous = true;
        foreach (var bar in bars)
        {
            var fill = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            fill.AddCssClass("bar-fill");
            fill.AddCssClass(bar.IsHighlighted ? "highlight" : "plain");
            fill.HeightRequest = Math.Max(2, (int)Math.Round(bar.Fraction * height));
            fill.Valign = Gtk.Align.End;
            fill.Halign = Gtk.Align.Fill;

            // The spacer pushes the value and the bar to the bottom of the column, where the label sits under them.
            var value = Ui.Label(bar.Value, "caption", "numeric");
            value.Halign = Gtk.Align.Center;
            var label = Ui.Label(bar.Label, "caption", "dim-label");
            label.Halign = Gtk.Align.Center;
            var column = Ui.VBox(4, Gtk.Box.New(Gtk.Orientation.Vertical, 0).Expand(false, true), value, fill, label);
            column.HeightRequest = height + 56;
            column.Tip(bar.Tooltip);
            row.Append(column);
        }

        return row;
    }

    /// <summary>Message shown instead of a list when it is empty.</summary>
    public static Gtk.Label Empty(string text) => Ui.Muted(text).Start();

    /// <summary>A tag-like marker with a colour (a belt, a level, a status).</summary>
    public static Gtk.Label Pill(string text, string? tone = null)
    {
        var label = Ui.Chip(text);
        if (tone is not null) label.AddCssClass(tone);
        return label;
    }
}
