using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>
/// The accessibility rules of the two UIs, checked on their markup (neither UI can run in a unit test): a control the user can focus has a
/// name for assistive technology, the web client has a skip link and a main landmark and keeps the keyboard able to reach every control.
/// A new control that breaks a rule fails here instead of being found by somebody with a screen reader.
/// </summary>
public partial class AccessibilityMarkupTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("global.json not found above the test binaries.");
    }

    private static string Src(params string[] parts) => Path.Combine([Root, "src", .. parts]);

    private static string Read(params string[] parts) => File.ReadAllText(Src(parts));

    // ---- WPF ----

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The pages, windows and controls of the WPF client (not its themes, which only style controls).</summary>
    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(Src("Kyuplan.Client.Wpf"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order();

    private static bool HasAutomationName(XElement element) =>
        element.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name")
        || element.Elements().Any(c => c.Name.LocalName.EndsWith(".AutomationProperties.Name", StringComparison.Ordinal)
                                      || c.Name.LocalName == "AutomationProperties.Name");

    private static string Where(string file, XElement element) =>
        $"{Path.GetRelativePath(Root, file)}:{((System.Xml.IXmlLineInfo)element).LineNumber} <{element.Name.LocalName}>";

    /// <summary>Controls that show no text of their own, so a screen reader announces nothing for them without a name.</summary>
    private static readonly string[] NeedsName =
        ["ProgressBar", "ComboBox", "DatePicker", "TextBox", "PasswordBox", "RevealPasswordBox", "HintedTextBox", "NumberBox", "PermissionPicker"];

    [Fact]
    public void Every_wpf_input_and_busy_bar_has_an_automation_name()
    {
        var missing = new List<string>();
        foreach (var file in XamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(e => NeedsName.Contains(e.Name.LocalName)))
                if (!HasAutomationName(element)) missing.Add(Where(file, element));
        }

        Assert.True(missing.Count == 0, "Without AutomationProperties.Name:\n" + string.Join("\n", missing));
    }

    private static readonly string[] ButtonKinds = ["Button", "ToggleButton", "RepeatButton"];

    private static bool IsGlyph(string text) => text.StartsWith("&#x", StringComparison.Ordinal) || text.Any(c => c is >= '\uE000' and <= '\uF8FF');

    /// <summary>A text block that draws an icon of the icon font (by its text or by its style) and says nothing in words.</summary>
    private static bool IsIcon(XElement textBlock) =>
        textBlock.Attribute("Text")?.Value is { } text && IsGlyph(text)
        || textBlock.Attribute("Style")?.Value is { } style && (style.Contains("GlyphText") || style.Contains("IconTextBlock"));

    /// <summary>A button whose content is only an icon says nothing without a name.</summary>
    [Fact]
    public void Every_wpf_icon_button_has_an_automation_name()
    {
        var missing = new List<string>();
        foreach (var file in XamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var button in document.Descendants().Where(e => ButtonKinds.Contains(e.Name.LocalName)))
            {
                if (HasAutomationName(button)) continue;
                if (button.Attribute("Content")?.Value is { } content && !IsGlyph(content)) continue; // text, a binding or a localized string

                // Words: any text block that is not an icon (its text may come from a binding, a style or runs).
                if (button.Descendants(Presentation + "TextBlock").Any(t => !IsIcon(t))) continue;

                missing.Add(Where(file, button));
            }
        }

        Assert.True(missing.Count == 0, "Icon-only buttons without AutomationProperties.Name:\n" + string.Join("\n", missing));
    }

    [GeneratedRegex(@"AutomationProperties\.Name=""\{loc:Loc (?<key>\w+)\}""")]
    private static partial Regex AutomationNameKey();

    /// <summary>A name that is a missing key would be announced as the key (or as nothing).</summary>
    [Fact]
    public void The_localized_automation_names_exist_in_both_languages()
    {
        var keys = XamlFiles()
            .SelectMany(f => AutomationNameKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        foreach (var culture in new[] { System.Globalization.CultureInfo.InvariantCulture, new System.Globalization.CultureInfo("de") })
        foreach (var key in keys)
            Assert.False(string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString(key, culture)), $"{key} is missing for '{culture.Name}'");
    }

    [Fact]
    public void The_spin_buttons_clear_button_and_busy_bars_are_named_by_translated_texts()
    {
        var number = Read("Kyuplan.Client.Wpf", "Controls", "NumberBox.xaml");
        Assert.Contains("{loc:Loc Number_Increase}", number);
        Assert.Contains("{loc:Loc Number_Decrease}", number);

        var hinted = Read("Kyuplan.Client.Wpf", "Controls", "HintedTextBox.xaml");
        Assert.Contains("{loc:Loc Field_Clear}", hinted);
        Assert.DoesNotContain("AutomationProperties.Name=\"Clear\"", hinted);

        foreach (var key in new[] { "Number_Increase", "Number_Decrease", "Field_Clear", "Shell_Loading", "Shell_SkipToContent" })
        {
            var english = Strings.ResourceManager.GetString(key, System.Globalization.CultureInfo.InvariantCulture);
            var german = Strings.ResourceManager.GetString(key, new System.Globalization.CultureInfo("de"));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.False(string.IsNullOrWhiteSpace(german));
            Assert.NotEqual(english, german); // none of these is the same word in both languages
        }
    }

    // ---- Blazor ----

    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(Src("Kyuplan.Client.Blazor"), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order();

    [Fact]
    public void The_layout_has_a_skip_link_before_the_navigation_and_a_main_landmark_it_leads_to()
    {
        var layout = Read("Kyuplan.Client.Blazor", "Layout", "MainLayout.razor");

        var skip = layout.IndexOf("class=\"skip-link\"", StringComparison.Ordinal);
        Assert.True(skip >= 0, "no skip link");
        Assert.True(skip < layout.IndexOf("<aside", StringComparison.Ordinal), "the skip link must come before the navigation");
        Assert.Contains("@Strings.Shell_SkipToContent", layout);
        Assert.Contains("<main id=\"main\"", layout);
        Assert.Contains("tabindex=\"-1\"", layout[layout.IndexOf("<main", StringComparison.Ordinal)..]); // focusable by script, not by Tab
        Assert.Single(Regex.Matches(layout, "<main[ >]"));

        // The script that moves the focus there is part of the page.
        var script = Read("Kyuplan.Client.Blazor", "wwwroot", "js", "a11y.js");
        Assert.Contains("focusMain", script);
        Assert.Contains("getElementById('main')", script);
        Assert.Contains("ppA11y.focusMain", layout);
        Assert.Contains("<script src=\"js/a11y.js\"></script>", Read("Kyuplan.Client.Blazor", "wwwroot", "index.html"));
    }

    [Fact]
    public void The_fly_out_menus_are_groups_of_links_and_not_half_built_aria_menus()
    {
        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("role=\"menu\"", text);
            Assert.DoesNotContain("role=\"menuitem\"", text);
            Assert.DoesNotContain("aria-haspopup=\"menu\"", text);
        }

        var layout = Read("Kyuplan.Client.Blazor", "Layout", "MainLayout.razor");
        Assert.Equal(2, Regex.Matches(layout, "class=\"flyout\" role=\"group\" aria-label=\"@Strings\\.Nav_(Administration|Account)\"").Count);
    }

    [Fact]
    public void The_keyboard_can_close_the_fly_out_and_the_stylesheet_honours_it()
    {
        var script = Read("Kyuplan.Client.Blazor", "wwwroot", "js", "a11y.js");
        Assert.Contains("e.key === 'Escape'", script);
        Assert.Contains("data-dismissed", script);
        Assert.Contains("aria-current", script);
        Assert.Contains("e.key === 'ArrowDown' || e.key === 'ArrowRight' || e.key === 'Enter' || e.key === ' '", script); // opens and enters it

        var css = Read("Kyuplan.Client.Blazor", "wwwroot", "css", "app.css");
        Assert.Contains(".nav-group[data-dismissed] > .flyout { display: none !important; }", css);
        Assert.Contains(".skip-link:focus", css);
    }

    [Fact]
    public void The_download_button_is_named_and_stays_on_phones_for_the_android_app()
    {
        var layout = Read("Kyuplan.Client.Blazor", "Layout", "MainLayout.razor");
        Assert.Matches(@"<a class=""download-cta""[^>]*aria-label=""@string\.Format\(Strings\.Download_Button, system\)""", layout);

        // The phone rules hide the sidebar's texts; the download link must not be among the hidden items, only its text is.
        var css = Read("Kyuplan.Client.Blazor", "wwwroot", "css", "app.css");
        var phone = css[css.IndexOf("/* A slim icon strip on top", StringComparison.Ordinal)..];
        foreach (Match rule in Regex.Matches(phone, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var hidden = rule.Groups["selector"].Value.Split(',').Any(s => s.Trim() == ".download-cta");
            Assert.False(hidden && rule.Groups["body"].Value.Contains("display: none"), "The phone rules hide the download link.");
        }

        Assert.Contains(".download-cta span { display: none; }", phone);

        // The script that picks the platform knows the three systems that have an installer.
        var script = Read("Kyuplan.Client.Blazor", "wwwroot", "js", "platform.js");
        foreach (var id in new[] { ClientPlatform.Windows, ClientPlatform.Linux, ClientPlatform.Android })
            Assert.Contains($"return '{id}'", script);
    }

    [Fact]
    public void No_button_of_the_web_client_is_taken_out_of_the_tab_order()
    {
        foreach (var file in RazorFiles())
            Assert.DoesNotMatch(@"<(button|a|input|select|textarea)\b[^>]*\btabindex=""-1""", File.ReadAllText(file));
    }

    [GeneratedRegex(@"<button\b(?<attrs>[^>]*)>(?<inner>.*?)</button>", RegexOptions.Singleline)]
    private static partial Regex ButtonTag();

    [GeneratedRegex(@"<Icon\b[^>]*/>")]
    private static partial Regex IconTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [Fact]
    public void Every_icon_only_button_of_the_web_client_has_an_aria_label()
    {
        var missing = new List<string>();
        foreach (var file in RazorFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ButtonTag().Matches(text))
            {
                if (match.Groups["attrs"].Value.Contains("aria-label", StringComparison.Ordinal)) continue;
                var visible = AnyTag().Replace(IconTag().Replace(match.Groups["inner"].Value, ""), "").Trim();
                if (visible.Length == 0)
                    missing.Add($"{Path.GetRelativePath(Root, file)}:{text[..match.Index].Count(c => c == '\n') + 1}");
            }
        }

        Assert.True(missing.Count == 0, "Icon-only buttons without aria-label:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_error_bar_of_the_page_is_announced_and_can_be_dismissed_with_the_keyboard()
    {
        var index = Read("Kyuplan.Client.Blazor", "wwwroot", "index.html");
        Assert.Contains("<div id=\"blazor-error-ui\" role=\"alert\">", index);

        var script = Read("Kyuplan.Client.Blazor", "wwwroot", "js", "a11y.js");
        Assert.Contains("#blazor-error-ui .dismiss", script);
        Assert.Contains("e.key === 'Enter' || e.key === ' '", script);
    }
}
