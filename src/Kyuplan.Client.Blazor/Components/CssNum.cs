using System.Globalization;

namespace Kyuplan.Client.Blazor.Components;

/// <summary>Numbers for inline styles. CSS needs a dot, whatever the user's culture formats doubles with.</summary>
internal static class CssNum
{
    public static string Pct(double fraction) =>
        (Math.Clamp(fraction, 0, 1) * 100).ToString("0.#", CultureInfo.InvariantCulture);

    public static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
