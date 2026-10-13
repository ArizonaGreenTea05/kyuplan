namespace Kyuplan.Client.ViewModels;

/// <summary>The aliases of a user as a text box holds them: one per line (a user name cannot contain a line break).</summary>
public static class AliasLines
{
    public static string Join(IEnumerable<string> aliases) => string.Join('\n', aliases);

    public static IReadOnlyList<string> Split(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether two lists hold the same names, whatever the order they are written in.</summary>
    public static bool Same(IEnumerable<string> a, IEnumerable<string> b) => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
}
