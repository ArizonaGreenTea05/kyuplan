namespace Kyuplan.Tests;

/// <summary>
/// Choosing a language applies its culture to the whole process (<c>LanguageService.Select</c>), so the tests of the plugin view models that
/// switch between English and German must not run beside each other: one would turn the language under the other's assertions.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LanguageStateCollection
{
    public const string Name = "Language state";
}
