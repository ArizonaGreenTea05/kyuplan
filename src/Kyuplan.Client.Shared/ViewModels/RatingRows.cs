using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// One user's star evaluation of a member at an event, as it is listed: who gave it, how many stars, and when. An evaluation of 0 stars
/// is <see cref="IsZero"/>, which the clients show apart from "not evaluated" (a member nobody evaluated has no such row at all).
/// </summary>
public sealed class RatingRow(RatingDto rating, bool isMine) : ObservableObject
{
    public RatingDto Rating => rating;

    public int UserId => rating.UserId;

    /// <summary>The evaluation of the signed-in user, who can change or take it back.</summary>
    public bool IsMine => isMine;

    public int Stars => rating.Stars;

    public bool IsZero => rating.Stars == 0;

    /// <summary>"Ben" or "Ben (you)".</summary>
    public string UserText => isMine ? $"{rating.UserName} ({Strings.Rating_You})" : rating.UserName;

    /// <summary>"★★★☆☆".</summary>
    public string Glyphs => Display.StarGlyphs(rating.Stars);

    /// <summary>"3/5".</summary>
    public string ValueText => $"{rating.Stars}/{RatingRules.MaxStars}";

    public string WhenText => Display.Moment(rating.UpdatedAt);

    /// <summary>"Ben: 3 of 5 stars · 17 Oct 2026, 18:30".</summary>
    public string Tip => $"{UserText}: {string.Format(Strings.Rating_Choice, rating.Stars)} · {WhenText}";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// One button of the row of stars the signed-in user evaluates a member with: 0 to 5, or (<see cref="Stars"/> = <c>null</c>) the one that
/// takes the evaluation back. Chosen with <c>CalendarViewModel.RateCommand</c>.
/// </summary>
public sealed class StarChoice(EventAttendeeRow row, int? stars) : ObservableObject
{
    public EventAttendeeRow Row => row;

    /// <summary>0 to 5; <c>null</c> = take the evaluation back.</summary>
    public int? Stars => stars;

    public bool IsClear => stars is null;

    /// <summary>The user's evaluation is this one. 0 is selected when they gave 0 stars, and not when they gave none.</summary>
    public bool IsSelected => stars is not null && row.MyStars == stars;

    /// <summary>A selected 0: an evaluation, a poor one, which the clients mark.</summary>
    public bool IsSelectedZero => IsSelected && stars == 0;

    public bool IsZero => stars == 0;

    /// <summary>A star up to the user's evaluation, so that the picker reads as a row of stars.</summary>
    public bool IsFilled => stars is > 0 && row.MyStars >= stars;

    /// <summary>"★" or "☆" for 1 to 5, "⊘" for the poor 0.</summary>
    public string Glyph => stars switch { 0 => "⊘", null => Text, _ => IsFilled ? "★" : "☆" };

    /// <summary>"3", or "Take back".</summary>
    public string Text => stars is { } value ? value.ToString() : Strings.Rating_Clear;

    /// <summary>"3 of 5 stars", or "Take your evaluation back".</summary>
    public string Tip => stars is { } value ? string.Format(Strings.Rating_Choice, value) : Strings.Rating_ClearTip;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
