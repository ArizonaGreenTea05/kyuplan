using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Tests;

/// <summary>
/// The desktop client draws the SVG logos itself, and its renderer would also load what a file points at. A logo is uploaded by an
/// administrator and drawn on every user's machine, so a file that refers to anything outside of itself is not drawn.
/// </summary>
public sealed class SvgSafetyTests
{
    private const string Open = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10">""";

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"/>""")]
    [InlineData("""<?xml version="1.0" encoding="UTF-8"?><!-- made with Inkscape --><svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1"/></svg>""")]
    // what Inkscape writes: clip paths and gradients are addressed by #id
    [InlineData(Open + """<defs><clipPath id="c"><rect width="5" height="5"/></clipPath><linearGradient id="g"/></defs><g clip-path="url(#c)"><rect fill="url(#g)" style="fill:url(#g);stroke:none"/></g></svg>""")]
    [InlineData(Open + """<use xlink:href="#shape"/><use href="#shape"/></svg>""")]
    [InlineData(Open + """<image width="1" height="1" xlink:href="data:image/png;base64,iVBORw0KGgo="/></svg>""")]
    [InlineData(Open + """<style>.a { fill: url("#g"); } .b { fill: url('data:image/png;base64,AAAA') }</style></svg>""")]
    [InlineData(Open + """<a href="https://example.org/"><rect width="1" height="1"/></a></svg>""")] // a link is only followed when somebody clicks it
    [InlineData(Open + """<text>The url(...) of the club, and @import</text></svg>""")] // the text of a logo is only text
    public void An_svg_that_carries_everything_it_draws_is_accepted(string svg) =>
        Assert.True(SvgSafety.IsSelfContained(svg));

    [Theory]
    [InlineData(Open + """<image width="1" height="1" xlink:href="file:///C:/Windows/win.ini"/></svg>""")]
    [InlineData(Open + """<image width="1" height="1" href="https://example.org/tracker.png"/></svg>""")]
    [InlineData(Open + """<image width="1" height="1" xlink:href="//example.org/x.png"/></svg>""")]
    [InlineData(Open + """<image width="1" height="1" xlink:href="other.svg"/></svg>""")]
    [InlineData(Open + """<use xlink:href="other.svg#shape"/></svg>""")]
    [InlineData(Open + """<rect fill="url(https://example.org/paint.svg#g)"/></svg>""")]
    [InlineData(Open + """<rect style="fill: URL( 'file:///C:/x.svg#g' )"/></svg>""")]
    [InlineData(Open + """<style>@import url(https://example.org/a.css);</style></svg>""")]
    [InlineData(Open + """<style>@import "a.css";</style></svg>""")]
    [InlineData(Open + """<style><![CDATA[ .a { fill: url(a.svg#g) } ]]></style></svg>""")]
    public void An_svg_that_refers_to_something_outside_of_the_file_is_refused(string svg) =>
        Assert.False(SvgSafety.IsSelfContained(svg));

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<svg>")]
    [InlineData("<svg></rect>")]
    // an entity from the doctype is not expanded, so a file that uses one cannot be read (and nothing it names is fetched)
    [InlineData("""<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><svg xmlns="http://www.w3.org/2000/svg"><text>&x;</text></svg>""")]
    public void A_document_that_cannot_be_read_is_refused(string svg) =>
        Assert.False(SvgSafety.IsSelfContained(svg));

    [Fact]
    public void A_doctype_that_declares_nothing_is_skipped_and_not_followed()
    {
        const string svg = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"><rect width="1" height="1"/></svg>
            """;

        Assert.True(SvgSafety.IsSelfContained(svg));
    }
}
