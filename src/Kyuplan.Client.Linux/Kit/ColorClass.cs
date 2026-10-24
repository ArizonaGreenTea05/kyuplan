using System.Text;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>
/// Colours that come from data (a belt's colours, a tag's colour): GTK paints from CSS, so every colour that is used gets a class of its own
/// (<c>c-1a2b3c</c>) in a stylesheet of its own, which is loaded again when a new colour joins. A value that is not <c>#RRGGBB</c> gets no class.
/// </summary>
internal static class ColorClass
{
    private static readonly HashSet<string> Known = [];
    private static Gtk.CssProvider? _provider;

    /// <summary>The class that paints the background in <paramref name="hex"/>, or <c>null</c> when it is no colour.</summary>
    public static string? Background(string? hex)
    {
        if (!Kyuplan.Contracts.ThemePackRules.TryParseHex(hex?.Trim(), out _)) return null;

        var name = "c-" + hex!.Trim()[1..].ToLowerInvariant();
        if (Known.Add(name)) Reload();
        return name;
    }

    private static void Reload()
    {
        var display = Gdk.Display.GetDefault();
        if (display is null) return;

        if (_provider is not null) Gtk.StyleContext.RemoveProviderForDisplay(display, _provider);
        var css = new StringBuilder();
        foreach (var name in Known) css.AppendLine($".{name} {{ background-color: #{name[2..]}; }}");
        _provider = Gtk.CssProvider.New();
        _provider.LoadFromString(css.ToString());
        Gtk.StyleContext.AddProviderForDisplay(display, _provider, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
    }

    /// <summary>A small rectangle in the colour (a belt colour, a preset): the colour of an invalid value is not drawn.</summary>
    public static Gtk.Widget Swatch(string? hex, int width = 28, int height = 16)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        box.SetSizeRequest(width, height);
        box.Valign = Gtk.Align.Center;
        box.AddCssClass("swatch");
        if (Background(hex) is { } name) box.AddCssClass(name);
        return box;
    }

    /// <summary>A belt as it is worn: the colour with a stripe of the second colour through its middle.</summary>
    public static Gtk.Widget Belt(string? primary, string? secondary, int width = 44, int height = 14)
    {
        var overlay = Gtk.Overlay.New();
        overlay.SetChild(Swatch(primary, width, height));
        if (!string.IsNullOrWhiteSpace(secondary) && Background(secondary) is { } stripe)
        {
            var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            line.SetSizeRequest(width, Math.Max(3, height / 3));
            line.Valign = Gtk.Align.Center;
            line.AddCssClass(stripe);
            overlay.AddOverlay(line);
        }

        overlay.Valign = Gtk.Align.Center;
        return overlay;
    }
}
