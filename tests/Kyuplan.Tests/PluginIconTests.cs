using Kyuplan.Client.Blazor.Components;

namespace Kyuplan.Tests;

/// <summary>The symbol of a page of a plugin: a name the web client knows (the plugin documentation lists them), else the default instead of an empty space.</summary>
public sealed class PluginIconTests
{
    [Theory]
    [InlineData("dashboard")]
    [InlineData("chart")]
    [InlineData("clock")]
    [InlineData("calendar")]
    [InlineData("members")]
    [InlineData("belt")]
    [InlineData("tag")]
    [InlineData("sliders")]
    [InlineData("users")]
    [InlineData("shield")]
    [InlineData("palette")]
    [InlineData("puzzle")]
    public void The_symbols_the_plugin_documentation_lists_exist(string name) => Assert.True(Icon.Exists(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cog")]
    [InlineData("chart ")]
    [InlineData("CHART")]
    public void A_name_the_web_client_does_not_know_has_no_symbol(string? name) => Assert.False(Icon.Exists(name));
}
