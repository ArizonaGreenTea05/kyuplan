using Kyuplan.Client.LinuxApp.Kit;

namespace Kyuplan.Client.LinuxApp.Stages;

/// <summary>The frame of the screens that are shown before the application: a header bar and a column in the middle.</summary>
internal static class Frame
{
    public static Gtk.Widget Centered(Gtk.Widget content, int width = 440)
    {
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = width;
        clamp.TighteningThreshold = width - 60;
        clamp.Valign = Gtk.Align.Center;
        clamp.SetChild(content.Margin(24, 18));

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(clamp);
        scroll.Vexpand = true;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;

        var view = Adw.ToolbarView.New();
        var header = Adw.HeaderBar.New();
        header.ShowTitle = false;
        view.AddTopBar(header);
        view.SetContent(scroll);
        return view;
    }
}
