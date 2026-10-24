using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>How this instance presents itself: name, accent colour, default themes, logos, and the theme packs.</summary>
internal static class BrandingPage
{
    /// <summary>The file extensions of the content types a slot takes: the browser's <c>accept</c> list, as a file dialog wants it.</summary>
    private static readonly Dictionary<string, string[]> Extensions = new()
    {
        [BrandingSlots.Png] = ["*.png"],
        [BrandingSlots.Jpeg] = ["*.jpg", "*.jpeg"],
        [BrandingSlots.Gif] = ["*.gif"],
        [BrandingSlots.Bmp] = ["*.bmp"],
        [BrandingSlots.Svg] = ["*.svg"],
        [BrandingSlots.Ico] = ["*.ico"],
    };

    public static Gtk.Widget Build(UiContext context, BrandingAdminViewModel page)
    {
        var state = Ui.Label(page, () => page.IsCustomized ? Strings.Branding_StateCustom : Strings.Branding_StateDefault, [nameof(BrandingAdminViewModel.IsCustomized)], "dim-label");

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Branding_Lede)),
            state,
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Texts(page), Images(context, page), Themes(context, page),
            Reset(page));
        return Widgets.PageFrame(page, body, 1000);
    }

    private static Gtk.Widget Problem(BrandingAdminViewModel page, Gtk.Widget field, Func<string?> problem, string property) =>
        Ui.VBox(2, field, Ui.Label(page, problem, [property], "error", "caption").VisibleWhen(page, () => !string.IsNullOrEmpty(problem()), property));

    private static Gtk.Widget Texts(BrandingAdminViewModel page)
    {
        var accent = Ui.HBox(8, Ui.Entry(page, nameof(BrandingAdminViewModel.FormAccentColor), name: Strings.Branding_AccentColor));
        var presets = Ui.Flow();
        foreach (var color in BrandingAdminViewModel.AccentPresets)
        {
            var button = Gtk.Button.New();
            button.SetChild(ColorClass.Swatch(color, 22, 22));
            button.AddCssClass("flat");
            button.Tip(color);
            button.OnClicked += (_, _) => page.PickAccentColorCommand.Execute(color);
            presets.Append(button);
        }

        var web = Ui.ComboRow(page, Strings.Branding_DefaultWebTheme, () => page.WebThemes, t => t.Name, () => page.WebThemes.FirstOrDefault(t => t.Id == page.FormDefaultWebTheme),
            t => page.FormDefaultWebTheme = t.Id, nameof(BrandingAdminViewModel.FormDefaultWebTheme), nameof(BrandingAdminViewModel.WebThemes));
        var windows = Ui.ComboRow(page, Strings.Branding_DefaultWindowsTheme, () => page.WindowsThemes, t => t.Name, () => page.WindowsThemes.FirstOrDefault(t => t.Id == page.FormDefaultWindowsTheme),
            t => page.FormDefaultWindowsTheme = t.Id, nameof(BrandingAdminViewModel.FormDefaultWindowsTheme), nameof(BrandingAdminViewModel.WindowsThemes));

        return Widgets.Section(Strings.Branding_TextsTitle,
            Problem(page, Ui.Field(Strings.Branding_ProductName, Ui.Entry(page, nameof(BrandingAdminViewModel.FormProductName), name: Strings.Branding_ProductName), Strings.Branding_ProductNameHint), () => page.ProductNameProblem, nameof(BrandingAdminViewModel.ProductNameProblem)),
            Problem(page, Ui.Field(Strings.Branding_OrganizationName, Ui.Entry(page, nameof(BrandingAdminViewModel.FormOrganizationName), name: Strings.Branding_OrganizationName), Strings.Branding_OrganizationNameHint), () => page.OrganizationNameProblem, nameof(BrandingAdminViewModel.OrganizationNameProblem)),
            Problem(page, Ui.Field(Strings.Branding_Currency, Ui.Entry(page, nameof(BrandingAdminViewModel.FormCurrency), name: Strings.Branding_Currency), Strings.Branding_CurrencyHint), () => page.CurrencyProblem, nameof(BrandingAdminViewModel.CurrencyProblem)),
            Problem(page, Ui.Field(Strings.Branding_AccentColor, accent, Strings.Branding_AccentColorHint), () => page.AccentColorProblem, nameof(BrandingAdminViewModel.AccentColorProblem)),
            presets,
            Ui.Group(null, web, windows),
            Ui.Primary(Strings.Branding_Save, page.SaveCommand).Start().VisibleWhen(page, nameof(BrandingAdminViewModel.CanEdit)));
    }

    private static Gtk.Widget Images(UiContext context, BrandingAdminViewModel page)
    {
        var rows = Ui.Repeat(page, () => page.Images, row =>
        {
            var preview = RemoteImage.Picture(page, () => row.PreviewUrl, 56);
            var state = row.IsPending ? Strings.Branding_NotSavedYet : row.HasImage ? "" : row.EmptyText;
            var box = Ui.HBox(12, preview,
                Ui.VBox(2, Ui.Label(row.Title, "heading").Start(), Ui.Muted(row.Hint).Css("caption"), Ui.Muted(state).Css("caption")).Expand(),
                Ui.Button(row.HasImage ? Strings.Branding_Replace : Strings.Branding_Upload, () => _ = UploadAsync(context, page, row)).VisibleWhen(page, nameof(BrandingAdminViewModel.CanEdit)),
                Ui.Button(Strings.Branding_Remove, page.RemoveImageCommand, row).Tap(b => b.Visible = page.CanRemove(row)),
                Ui.Button(Strings.Branding_Undo, page.UndoImageCommand, row).Tap(b => b.Visible = page.CanUndo(row)));
            if (row.OnDark) preview.AddCssClass("on-dark-preview");
            return box.Css("card").Padded(10);
        }, [nameof(BrandingAdminViewModel.Images)], 8);

        return Widgets.Section(Strings.Branding_ImagesTitle,
            Ui.Label(page, nameof(BrandingAdminViewModel.ImagesLede), "dim-label"),
            rows,
            Ui.Primary(Strings.Branding_Save, page.SaveImagesCommand).Start().VisibleWhen(page, nameof(BrandingAdminViewModel.CanSaveImages)));
    }

    private static async Task UploadAsync(UiContext context, BrandingAdminViewModel page, BrandingImageRow row)
    {
        var patterns = row.Accept.Split(',', StringSplitOptions.RemoveEmptyEntries).SelectMany(type => Extensions.GetValueOrDefault(type, [])).ToArray();
        if (await context.PickFileAsync(row.Title, (Strings.Branding_ImagesTitle, string.Join(';', patterns))) is not { } file || file.GetPath() is not { } path) return;

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            page.ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return;
        }

        await page.PickImageAsync(row.Slot, size, () => File.OpenRead(path));
    }

    private static Gtk.Widget Themes(UiContext context, BrandingAdminViewModel page)
    {
        var packs = Ui.BoxedList(page, () => page.ThemePacks, pack =>
        {
            var row = Ui.Row(pack.Name, pack.Details);
            var delete = Ui.IconButton("user-trash-symbolic", Strings.Themes_Delete, page.DeleteThemeCommand, pack, "flat");
            delete.Visible = page.CanDeleteTheme(pack);
            row.AddSuffix(delete);
            return row;
        }, [nameof(BrandingAdminViewModel.ThemePacks)]);

        return Widgets.Section(Strings.Branding_ThemesTitle,
            Ui.Muted(Strings.Branding_ThemesLede),
            Ui.Muted(Strings.Themes_Lede).Css("caption"),
            packs,
            Ui.Button(Strings.Themes_Install, () => _ = InstallThemeAsync(context, page)).Start().VisibleWhen(page, nameof(BrandingAdminViewModel.CanManageThemes)));
    }

    /// <summary>Lets the user pick the JSON file of a theme pack; the view model refuses one that is too large without reading it, and the API says what is wrong with the others.</summary>
    private static async Task InstallThemeAsync(UiContext context, BrandingAdminViewModel page)
    {
        if (await context.PickFileAsync(Strings.Themes_Install, (Strings.Themes_Title, "*.json")) is not { } file || file.GetPath() is not { } path) return;

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            page.ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return;
        }

        await page.InstallThemeAsync(size, () => File.ReadAllTextAsync(path));
    }

    private static Gtk.Widget Reset(BrandingAdminViewModel page) =>
        Ui.VBox(8,
            Ui.Muted(Strings.Branding_ResetHint).Css("caption"),
            Ui.Button(Strings.Branding_Reset, page.AskResetCommand).Start().VisibleWhen(page, nameof(BrandingAdminViewModel.CanReset)),
            Ui.HBox(12, Ui.Label(Strings.Branding_ConfirmReset).Expand(),
                Ui.Button(Strings.Branding_ConfirmYes, page.ConfirmResetCommand, null, "destructive-action"), Ui.Button(Strings.Branding_Cancel, page.CancelResetCommand))
                .Css("banner-problem").VisibleWhen(page, nameof(BrandingAdminViewModel.IsConfirmingReset)));
}
