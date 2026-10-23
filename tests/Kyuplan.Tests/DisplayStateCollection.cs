namespace Kyuplan.Tests;

/// <summary>
/// <c>Display.Currency</c> is one static per process. Tests that set it (<c>BrandingViewModel</c> loading the branding) or compare
/// against it must not run in parallel with each other, or one resets the currency another is just asserting on.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DisplayStateCollection
{
    public const string Name = "Display state";
}
