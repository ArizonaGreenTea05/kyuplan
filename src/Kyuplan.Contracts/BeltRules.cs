namespace Kyuplan.Contracts;

/// <summary>
/// What a belt must look like. The API enforces it; the clients check the same rules so that they only offer to save what the API
/// will accept. Each check returns the problem in words, or <c>null</c> when it is fine.
/// </summary>
public static class BeltRules
{
    /// <summary>The length of the name column of the database.</summary>
    public const int MaxNameLength = 200;

    public const int MaxMinAgeYears = 99;
    public const int MaxMinWaitMonths = 240;

    public static string? CheckName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "name is required.";
        return trimmed.Length > MaxNameLength ? $"name must be at most {MaxNameLength} characters long." : null;
    }

    /// <summary>The colour of the belt: <c>#RRGGBB</c>, which goes straight into CSS and WPF brushes.</summary>
    public static string? CheckPrimaryColor(string? color) =>
        IsColor(color?.Trim()) ? null : "primaryColor must be a colour written #RRGGBB.";

    /// <summary>The colour of the stripe: nothing (no stripe) or <c>#RRGGBB</c>.</summary>
    public static string? CheckSecondaryColor(string? color) =>
        string.IsNullOrWhiteSpace(color) || IsColor(color.Trim()) ? null : "secondaryColor must be a colour written #RRGGBB, or left out.";

    public static string? CheckMinAgeYears(int years) =>
        years is >= 0 and <= MaxMinAgeYears ? null : $"minAgeYears must be between 0 and {MaxMinAgeYears}.";

    public static string? CheckMinWaitMonths(int months) =>
        months is >= 0 and <= MaxMinWaitMonths ? null : $"minWaitMonths must be between 0 and {MaxMinWaitMonths}.";

    /// <summary>True for <c>#RRGGBB</c>, in any case.</summary>
    public static bool IsColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit);
}

/// <summary>What the days of a member may be, see <see cref="BeltRules"/>.</summary>
public static class MemberRules
{
    /// <summary>Nobody started before this: it catches a mistyped year without anybody having to think about it.</summary>
    public static readonly DateOnly EarliestStart = new(1950, 1, 1);

    /// <summary>Nobody was born before this, for the same reason.</summary>
    public static readonly DateOnly EarliestBirth = new(1900, 1, 1);

    /// <summary>The start day is optional, but when it is there it is a day since <see cref="EarliestStart"/> that is not in the future.</summary>
    public static string? CheckStartedAt(DateOnly? day, DateOnly today)
    {
        if (day is not { } d) return null;
        if (d < EarliestStart) return $"startedAt must not be before {EarliestStart:yyyy-MM-dd}.";
        return d > today ? "startedAt must not be in the future." : null;
    }

    /// <summary>The day the member got their belt is optional, but when it is there it is a day since <see cref="EarliestStart"/> that is not in the future.</summary>
    public static string? CheckBeltSince(DateOnly? day, DateOnly today)
    {
        if (day is not { } d) return null;
        if (d < EarliestStart) return $"beltSince must not be before {EarliestStart:yyyy-MM-dd}.";
        return d > today ? "beltSince must not be in the future." : null;
    }

    /// <summary>The birth date is optional, but when it is there it is a day since <see cref="EarliestBirth"/> that is not in the future.</summary>
    public static string? CheckBirthDate(DateOnly? day, DateOnly today)
    {
        if (day is not { } d) return null;
        if (d < EarliestBirth) return $"birthDate must not be before {EarliestBirth:yyyy-MM-dd}.";
        return d > today ? "birthDate must not be in the future." : null;
    }
}
