using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>The logo of the instance, as the API says: the one for dark surfaces on a dark theme, the other on a light one. No logo, no space for it.</summary>
internal static class BrandLogo
{
    public static Gtk.Widget Create(BrandingViewModel brand, int height)
    {
        var onDark = RemoteImage.Picture(brand, () => brand.LogoOnDarkUrl, height, nameof(BrandingViewModel.LogoOnDarkUrl));
        var onLight = RemoteImage.Picture(brand, () => brand.LogoUrl, height, nameof(BrandingViewModel.LogoUrl));
        var box = Ui.VBox(0, onDark, onLight);

        var styles = Adw.StyleManager.GetDefault();
        void Choose()
        {
            onDark.Visible = styles.Dark;
            onLight.Visible = !styles.Dark;
        }

        Choose();
        void OnNotify(GObject.Object sender, GObject.Object.NotifySignalArgs e)
        {
            if (e.Pspec.GetName() == "dark") Choose();
        }

        styles.OnNotify += OnNotify;
        box.OnDestroy += (_, _) => styles.OnNotify -= OnNotify;
        box.VisibleWhen(brand, () => brand.HasLogo, nameof(BrandingViewModel.LogoUrl));
        box.Halign = Gtk.Align.Center;
        return box.Tip(brand.LogoDescription);
    }
}
