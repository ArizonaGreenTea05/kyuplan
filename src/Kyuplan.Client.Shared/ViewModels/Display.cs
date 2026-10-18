using System.Globalization;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// Text formatting shared by both clients. Done here, not in XAML/Razor, because WPF bindings format with
/// en-US unless told otherwise while this uses the user's culture. Wording and date patterns come from
/// <see cref="Strings"/>, so they follow the selected language.
/// </summary>
public static class Display
{
    /// <summary>What a cell shows when there is nothing to show.</summary>
    public const string None = "–";

    /// <summary>What a cell shows for a value that is asked for but not known.</summary>
    public const string Unknown = "?";

    /// <summary>
    /// The unit of money the administrators set (<see cref="BrandingDto.Currency"/>); <c>BrandingViewModel</c> keeps it current.
    /// There is one instance per client process, so a static is enough.
    /// </summary>
    public static string Currency { get; set; } = BrandingRules.DefaultCurrency;

    /// <summary>An amount of money with the instance's currency, number in the user's culture: "12.50 $", "12,50 €".</summary>
    public static string Money(decimal amount) => $"{amount.ToString("N2", CultureInfo.CurrentCulture)} {Currency}";

    /// <summary>An amount as it is typed into a field: "12.50", with the decimal separator of the user's culture.</summary>
    public static string MoneyInput(decimal amount) => amount.ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>Reads what a user typed as an amount of money (see <see cref="PaymentRules.IsAmount"/>); an empty text is 0.</summary>
    public static bool TryParseMoney(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;

        return decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out amount) && PaymentRules.IsAmount(amount);
    }

    public static string Percent(double value) => value.ToString("0.#", CultureInfo.CurrentCulture) + " %";

    /// <summary>An average of stars with one decimal: "3.4".</summary>
    public static string Stars(double value) => value.ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>"★★★☆☆": the stars given, then the empty ones up to five.</summary>
    public static string StarGlyphs(int stars) =>
        new string('★', Math.Clamp(stars, 0, RatingRules.MaxStars)) + new string('☆', RatingRules.MaxStars - Math.Clamp(stars, 0, RatingRules.MaxStars));

    public static string Number(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    public static string Date(DateOnly date) => date.ToString(Strings.Format_Date, CultureInfo.CurrentCulture);

    public static string Date(DateTimeOffset date) => date.ToString(Strings.Format_Date, CultureInfo.CurrentCulture);

    public static string EventStart(DateTimeOffset start) =>
        start.ToString(Strings.Format_EventStart, CultureInfo.CurrentCulture);

    /// <summary>"Sat, 26 Sep 2026": the day of an event with its year, which <see cref="EventStart"/> leaves out.</summary>
    public static string EventDay(DateTimeOffset start) =>
        start.ToString(Strings.Format_EventDay, CultureInfo.CurrentCulture);

    public static string Time(DateTimeOffset start) => start.ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"15 Oct 2026, 09:12": a moment, e.g. when a comment was written; the club's time zone is the one the API gave it in.</summary>
    public static string Moment(DateTimeOffset moment) => moment.ToString(Strings.Format_Moment, CultureInfo.CurrentCulture);

    public static string Month(DateOnly month) => month.ToString(Strings.Format_Month, CultureInfo.CurrentCulture);

    public static string Period(DateOnly from, DateOnly to) => $"{Date(from)} – {Date(to)}";

    public static string Level(EngagementLevel level) => level switch
    {
        EngagementLevel.Excellent => Strings.Level_Excellent,
        EngagementLevel.Good => Strings.Level_Good,
        EngagementLevel.Irregular => Strings.Level_Irregular,
        _ => Strings.Level_AtRisk,
    };

    public static string Status(QualificationStatus status) => status switch
    {
        QualificationStatus.Qualified => Strings.Status_Qualified,
        QualificationStatus.BelowThreshold => Strings.Status_BelowThreshold,
        _ => Strings.Status_TooFew,
    };

    /// <summary>The longer explanation of a status, for tooltips.</summary>
    public static string StatusTip(QualificationStatus status) => status switch
    {
        QualificationStatus.Qualified => Strings.StatusTip_Qualified,
        QualificationStatus.BelowThreshold => Strings.StatusTip_BelowThreshold,
        _ => Strings.StatusTip_TooFew,
    };

    public static string Verdict(SuggestionVerdict verdict) => verdict switch
    {
        SuggestionVerdict.Fit => Strings.Plan_VerdictFit,
        SuggestionVerdict.NeedsData => Strings.Plan_VerdictNeedsData,
        SuggestionVerdict.NotYet => Strings.Plan_VerdictNotYet,
        _ => Strings.Plan_VerdictNoNext,
    };

    /// <summary>The longer explanation of a verdict, for tooltips.</summary>
    public static string VerdictTip(SuggestionVerdict verdict) => verdict switch
    {
        SuggestionVerdict.Fit => Strings.Plan_VerdictTipFit,
        SuggestionVerdict.NeedsData => Strings.Plan_VerdictTipNeedsData,
        SuggestionVerdict.NotYet => Strings.Plan_VerdictTipNotYet,
        _ => Strings.Plan_VerdictTipNoNext,
    };

    /// <summary>What a member answered to the invitation to an exam; <c>null</c> = they were not invited (yet).</summary>
    public static string Invitation(AttendanceMark? answer) => answer switch
    {
        null => Strings.Plan_NotInvited,
        AttendanceMark.Attended => Strings.Plan_InvAccepted,
        AttendanceMark.Declined => Strings.Plan_InvDeclined,
        AttendanceMark.Unanswered => Strings.Plan_InvNoAnswer,
        _ => Strings.Plan_InvOther,
    };

    public static string Mark(AttendanceMark mark) => mark switch
    {
        AttendanceMark.Attended => Strings.Mark_Attended,
        AttendanceMark.Declined => Strings.Mark_Declined,
        AttendanceMark.Unanswered => Strings.Mark_NoAnswer,
        _ => Strings.Mark_Other,
    };

    public static string TrendGlyph(AttendanceTrend trend) => trend switch
    {
        AttendanceTrend.Rising => "▲",
        AttendanceTrend.Falling => "▼",
        AttendanceTrend.Stable => "▬",
        _ => "–",
    };

    public static string TrendText(AttendanceTrend trend, double? delta) => trend switch
    {
        AttendanceTrend.Unknown => Strings.Trend_Unknown,
        _ => string.Format(Strings.Trend_Delta, TrendGlyph(trend), (delta >= 0 ? "+" : "") + Number(delta ?? 0)),
    };
}
