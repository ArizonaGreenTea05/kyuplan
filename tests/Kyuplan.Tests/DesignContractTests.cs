using System.Text.RegularExpressions;

namespace Kyuplan.Tests;

/// <summary>
/// The designs of the WPF client (Themes/Design.*.xaml, see docs/reference.md, "Look"). The pages are the same XAML in every design and ask the design for
/// names (Fluent's: <c>TextFillColorPrimaryBrush</c>, <c>AccentButtonStyle</c>, ...), so a name a page uses that one design lacks only
/// shows when somebody picks that design and opens that page. These tests read the XAML as text and catch it on Linux, too.
/// </summary>
public partial class DesignContractTests
{
    private static readonly string Xaml = Path.Combine(AppContext.BaseDirectory, "WpfXaml");

    /// <summary>What Fluent provides to the pages by itself and what every other design therefore has to provide.</summary>
    private static readonly string[] FluentNames =
    [
        // brushes
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "ControlStrongStrokeColorDefaultBrush",
        "CardStrokeColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", "SubtleFillColorSecondaryBrush", "DividerStrokeColorDefaultBrush",
        "SolidBackgroundFillColorBaseBrush", "LayerFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ComboBoxDropDownBorderBrush",
        "ComboBoxDropDownBackground",
        // shapes, fonts
        "ControlCornerRadius", "OverlayCornerRadius", "SymbolThemeFontFamily",
        // styles
        "AccentButtonStyle", "DefaultButtonStyle", "DefaultToggleButtonStyle", "DefaultListBoxItemStyle", "DefaultRepeatButtonStyle",
        "DefaultDataGridColumnHeaderStyle", "DefaultDataGridCellStyle", "BodyTextBlockStyle", "BodyStrongTextBlockStyle", "CaptionTextBlockStyle",
        "SubtitleTextBlockStyle", "TitleTextBlockStyle", "DefaultControlFocusVisualStyle",
    ];

    /// <summary>The app's own tokens, which Styles.xaml and the windows read in every design (Fluent's included).</summary>
    private static readonly string[] AppTokens =
    [
        "AppNavPaneBrush", "AppNavSelectedBorderBrush", "AppNavIndicatorVisibility", "AppContentCornerRadius", "AppContentBorderThickness",
        "AppPillCornerRadius", "AppSwatchCornerRadius", "AppTabCornerRadius", "AppTabBorderThickness", "AppTabPadding", "AppTabSelectedBrush",
        "AppTabHoverBrush",
    ];

    /// <summary>Made by ThemeService from the accent colour (or, for the Fluent theme, by the theme itself), so no file defines them.</summary>
    private static readonly string[] MadeInCode =
    [
        "AccentFillColorDefaultBrush", "AccentFillColorSelectedTextBackgroundBrush", "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush", "TextOnAccentFillColorPrimaryBrush", "AppTabSelectedBorderBrush",
        "CtlInputFocusBorder", "CtlAccentButtonBackground", "CtlAccentButtonHoverBackground", "CtlAccentButtonPressedBackground",
        "CtlAccentButtonBorder", "CtlAccentButtonHoverBorder", "CtlAccentButtonPressedBorder", "CtlAccentButtonInnerBorder",
        "CtlToggleCheckedBackground", "CtlToggleCheckedBorder", "CtlToggleCheckedForeground", "CtlListSelectedBackground", "CtlListSelectedBorder",
        "CtlCheckedBackground", "CtlCheckedBorder", "CtlCheckMarkBrush", "CtlRadioDotBrush", "CtlProgressFill", "CtlSliderFill",
    ];

    /// <summary>The files that make up each design, by name; Windows 10 comes in a light and a dark colour set.</summary>
    public static TheoryData<string, string[]> Designs => new()
    {
        { "Windows 11", ["Themes/Design.Fluent.xaml"] },
        { "Windows 10 light", ["Themes/Design.Win10.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Win10.Light.xaml"] },
        { "Windows 10 dark", ["Themes/Design.Win10.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Win10.Dark.xaml"] },
        { "Aero", ["Themes/Design.Aero.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Aero.xaml"] },
        { "Native", ["Themes/Design.Native.xaml"] },
        { "Dracula", ["Themes/Design.Modern.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Dracula.xaml"] },
        { "Catppuccin Mocha", ["Themes/Design.Modern.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.CatppuccinMocha.xaml"] },
        { "Catppuccin Latte", ["Themes/Design.Modern.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.CatppuccinLatte.xaml"] },
        { "Liquid Glass light", ["Themes/Design.Glass.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Glass.Light.xaml"] },
        { "Liquid Glass dark", ["Themes/Design.Glass.xaml", "Themes/Controls.Classic.xaml", "Themes/Colors.Glass.Dark.xaml"] },
    };

    [GeneratedRegex(@"x:Key=""([^""]+)""")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\{(?:StaticResource|DynamicResource)\s+([A-Za-z0-9_]+)\s*\}")]
    private static partial Regex ReferencePattern();

    private static string Read(string file) => File.ReadAllText(Path.Combine(Xaml, file));

    private static HashSet<string> Defined(params string[] files) =>
        [.. files.SelectMany(f => KeyPattern().Matches(Read(f)).Select(m => m.Groups[1].Value))];

    private static IEnumerable<string> AllXaml() =>
        Directory.EnumerateFiles(Xaml, "*.xaml", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Xaml, f).Replace('\\', '/'));

    private static bool IsDesignFile(string file) =>
        file.StartsWith("Themes/Design.") || file.StartsWith("Themes/Colors.") || file == "Themes/Controls.Classic.xaml";

    [Theory]
    [MemberData(nameof(Designs))]
    public void Every_design_has_the_tokens_of_the_app(string design, string[] files)
    {
        var defined = Defined(files);
        // The colour files are what makes a design light or dark; the tokens may be in either file of it.
        Assert.Empty(AppTokens.Where(t => !defined.Contains(t) && !MadeInCode.Contains(t)).Select(t => $"{design}: {t}"));
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void The_designs_without_Fluent_have_every_name_the_pages_ask_Fluent_for(string design, string[] files)
    {
        if (design == "Windows 11") return; // Fluent's own theme provides them

        var defined = Defined(files);
        Assert.Empty(FluentNames.Where(n => !defined.Contains(n) && !MadeInCode.Contains(n)).Select(n => $"{design}: {n}"));
    }

    /// <summary>A page that starts to use another Fluent name would break the designs that have no Fluent theme.</summary>
    [Fact]
    public void Pages_only_use_names_the_app_defines_or_the_designs_have_to_provide()
    {
        var own = AllXaml().Where(f => !IsDesignFile(f)).SelectMany(f => Defined(f)).ToHashSet();
        var allowed = own.Concat(FluentNames).Concat(AppTokens).Concat(MadeInCode).ToHashSet();

        var unknown = AllXaml()
            .Where(f => !IsDesignFile(f))
            .SelectMany(f => ReferencePattern().Matches(Read(f)).Select(m => (File: f, Key: m.Groups[1].Value)))
            .Where(r => !allowed.Contains(r.Key))
            .Select(r => $"{r.File}: {r.Key}")
            .Distinct();

        Assert.Empty(unknown);
    }

    /// <summary>The templates of the Windows 10 and Aero designs read their shapes and colours from the design: none may be missing.</summary>
    [Theory]
    [MemberData(nameof(Designs))]
    public void The_classic_templates_find_everything_they_read(string design, string[] files)
    {
        if (!files.Contains("Themes/Controls.Classic.xaml")) return;

        var defined = Defined(files);
        var missing = ReferencePattern().Matches(Read("Themes/Controls.Classic.xaml")).Select(m => m.Groups[1].Value)
            .Where(k => !defined.Contains(k) && !MadeInCode.Contains(k) && !FluentNames.Contains(k))
            .Distinct()
            .Select(k => $"{design}: {k}");

        Assert.Empty(missing);
    }

    /// <summary>Every colour set swaps in for another one while the program runs (Dracula to Catppuccin, light to dark), so each must define the same names.</summary>
    [Theory]
    [InlineData("Themes/Colors.Dracula.xaml")]
    [InlineData("Themes/Colors.CatppuccinMocha.xaml")]
    [InlineData("Themes/Colors.CatppuccinLatte.xaml")]
    [InlineData("Themes/Colors.Glass.Light.xaml")]
    [InlineData("Themes/Colors.Glass.Dark.xaml")]
    public void The_colours_of_every_theme_define_the_same_names_as_the_ones_of_Windows_10(string file) =>
        Assert.Equal(Defined("Themes/Colors.Win10.Dark.xaml").OrderBy(k => k), Defined(file).OrderBy(k => k));

    [Fact]
    public void The_light_and_the_dark_colours_of_Windows_10_define_the_same_names() =>
        Assert.Equal(
            Defined("Themes/Colors.Win10.Light.xaml").OrderBy(k => k),
            Defined("Themes/Colors.Win10.Dark.xaml").OrderBy(k => k));
}
