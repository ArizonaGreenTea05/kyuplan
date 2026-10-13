namespace Kyuplan.Contracts;

/// <summary>
/// The languages the clients are translated into, as the account stores them (<see cref="UserDto.Language"/>). Keep in step with
/// <c>LanguageService.Supported</c> in Client.Shared (a test checks it).
/// </summary>
public static class LanguageRules
{
    public const int MaxLength = 8;

    /// <summary>The language codes; the first one is the fallback.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["en", "de"];

    public static bool IsValid(string? code) => code is not null && Codes.Contains(code);

    /// <summary>What is wrong with a language code, or <c>null</c>.</summary>
    public static string? Check(string? code, string field = "language") =>
        IsValid(code) ? null : $"{field} must be one of: {string.Join(", ", Codes)}.";
}

/// <summary>What a user sends to choose their language (<c>PUT /api/account/language</c>).</summary>
/// <param name="Language">One of <see cref="LanguageRules.Codes"/>, or <c>null</c> to forget the choice (the client follows its system's language again).</param>
public sealed record LanguagePreferenceDto(string? Language);
