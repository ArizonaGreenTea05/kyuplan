using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Tests;

/// <summary>
/// The shades the WPF client makes of the API's accent colour. The source file is compiled into this project (see the csproj):
/// the WPF project targets Windows and cannot be referenced from here.
/// </summary>
public class AccentShadesTests
{
    [Theory]
    [InlineData("#00a650", 0x00, 0xa6, 0x50)]
    [InlineData("#00A650", 0x00, 0xa6, 0x50)]
    [InlineData("#1a5fb4", 0x1a, 0x5f, 0xb4)]
    [InlineData("#16b", 0x11, 0x66, 0xbb)] // #RGB doubles every digit, as in CSS
    public void Both_forms_the_api_accepts_are_read(string hex, int r, int g, int b)
    {
        Assert.True(Rgb.TryParse(hex, out var rgb));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), rgb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("00a650")] // no hash
    [InlineData("#00a65")]
    [InlineData("#00a6500")]
    [InlineData("#00a65g")]
    [InlineData("#ff00a650")] // no alpha: the API does not take one
    [InlineData("green")]
    public void Anything_else_is_not_a_colour(string? hex) => Assert.False(Rgb.TryParse(hex, out _));

    /// <summary>What Windows itself makes of its purple accent (#B146C2): the factors are fitted to these.</summary>
    [Fact]
    public void The_shades_are_close_to_what_Windows_makes_of_its_own_accent()
    {
        var purple = new Rgb(0xB1, 0x46, 0xC2);

        var light = AccentShades.Of(purple, dark: false);
        AssertClose(new Rgb(0xB1, 0x46, 0xC2), light.Selection);
        AssertClose(new Rgb(0x9E, 0x3A, 0xB0), light.Fill); // Windows' AccentDark1
        AssertClose(new Rgb(0x6F, 0x23, 0x82), light.Text); // AccentDark2
        AssertClose(new Rgb(0x46, 0x0D, 0x5A), light.TextSecondary); // AccentDark3

        var dark = AccentShades.Of(purple, dark: true);
        AssertClose(new Rgb(0xB1, 0x46, 0xC2), dark.Selection);
        AssertClose(new Rgb(0xD8, 0x8D, 0xE1), dark.Fill); // AccentLight2
        AssertClose(new Rgb(0xF1, 0xBB, 0xF4), dark.Text); // AccentLight3
        AssertClose(new Rgb(0xF1, 0xBB, 0xF4), dark.TextSecondary);
    }

    [Fact]
    public void On_a_light_theme_fills_are_darker_and_on_a_dark_theme_lighter_than_the_accent_itself()
    {
        var accent = new Rgb(0x00, 0xa6, 0x50);

        var light = AccentShades.Of(accent, dark: false);
        Assert.True(light.Fill.G < accent.G && light.Text.G < light.Fill.G && light.TextSecondary.G < light.Text.G);

        var dark = AccentShades.Of(accent, dark: true);
        Assert.True(dark.Fill.R > accent.R && dark.Text.R > dark.Fill.R);
    }

    [Fact]
    public void Black_and_white_stay_inside_the_colour_range()
    {
        Assert.Equal(new Rgb(0, 0, 0), new Rgb(0, 0, 0).Darker(0.5));
        Assert.Equal(new Rgb(255, 255, 255), new Rgb(255, 255, 255).Lighter(0.7));
        Assert.Equal(new Rgb(255, 255, 255), new Rgb(0, 0, 0).Lighter(1));
    }

    private static void AssertClose(Rgb expected, Rgb actual, int tolerance = 16)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
    }
}
