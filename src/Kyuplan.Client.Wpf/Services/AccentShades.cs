namespace Kyuplan.Client.WpfApp.Services;

/// <summary>A colour without alpha. No WPF type, so that the shades below can be tested outside the Windows desktop framework.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary><c>#RGB</c> or <c>#RRGGBB</c>, the two forms the API accepts for <c>Branding:AccentColor</c>.</summary>
    public static bool TryParse(string? hex, out Rgb rgb)
    {
        rgb = default;
        if (hex is not ['#', .. var digits] || digits.Length is not (3 or 6) || !digits.All(Uri.IsHexDigit)) return false;

        if (digits.Length == 3) digits = string.Concat(digits.Select(d => $"{d}{d}"));
        rgb = new Rgb(Convert.ToByte(digits[..2], 16), Convert.ToByte(digits[2..4], 16), Convert.ToByte(digits[4..], 16));
        return true;
    }

    /// <summary>The colour scaled towards black: <paramref name="keep"/> 1 is unchanged, 0 is black.</summary>
    public Rgb Darker(double keep) => new(Scale(R, keep), Scale(G, keep), Scale(B, keep));

    /// <summary>The colour mixed with white: <paramref name="amount"/> 0 is unchanged, 1 is white.</summary>
    public Rgb Lighter(double amount) => new(Mix(R, amount), Mix(G, amount), Mix(B, amount));

    private static byte Scale(byte channel, double keep) => (byte)Math.Round(channel * keep);

    private static byte Mix(byte channel, double amount) => (byte)Math.Round(channel + (255 - channel) * amount);
}

/// <summary>
/// The four shades of an accent colour that the Fluent theme draws with, made from one colour the way Windows makes them from the
/// user's accent colour: on a light theme fills are a little darker than the colour and text a lot darker, on a dark theme fills are
/// lighter and text lighter still. The factors are fitted to what Windows produces for its own accent colours (within about 15 of 255 per channel).
/// </summary>
/// <param name="Selection">The colour itself: selected text, selected list items.</param>
/// <param name="Fill">Buttons, checked boxes, switches, the marker of the selected page, focus lines.</param>
/// <param name="Text">Links and other text in the accent colour.</param>
/// <param name="TextSecondary">Hovered links.</param>
public sealed record AccentShades(Rgb Selection, Rgb Fill, Rgb Text, Rgb TextSecondary)
{
    public static AccentShades Of(Rgb accent, bool dark) => dark
        ? new(accent, accent.Lighter(0.45), accent.Lighter(0.70), accent.Lighter(0.70))
        : new(accent, accent.Darker(0.89), accent.Darker(0.63), accent.Darker(0.39));
}
