using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the exam list: the exam plus ready-to-show texts, which follow the language after <see cref="Refresh"/>.</summary>
public sealed class ExamRow(ExamDto exam, DateOnly today) : ObservableObject
{
    public ExamDto Exam => exam;

    public int Id => exam.Id;

    public string Name => exam.Name;

    public DateOnly Date => exam.Date;

    public string DateText => Display.Date(exam.Date);

    /// <summary>"Sat": the weekday, for the calendar page that shows the day.</summary>
    public string Weekday => exam.Date.ToString("ddd", CultureInfo.CurrentCulture);

    /// <summary>"26": the day of the month.</summary>
    public string DayNumber => exam.Date.ToString("%d", CultureInfo.CurrentCulture);

    /// <summary>"Sep": the month.</summary>
    public string MonthText => exam.Date.ToString("MMM", CultureInfo.CurrentCulture);

    /// <summary>"Gürtelprüfung · 26 Sep 2026": tells exams of the same name apart, e.g. in a list of saved filters.</summary>
    public string Label => $"{exam.Name} · {DateText}";

    /// <summary>The exam is today or still to come, so who is fit for it can be planned. (Today's exam can also be given results.)</summary>
    public bool IsUpcoming => exam.Date >= today;

    public bool IsPast => exam.Date < today;

    /// <summary>"Upcoming" or "Past": today's exam still counts as upcoming.</summary>
    public string StatusText => IsUpcoming ? Strings.Exams_Upcoming : Strings.Exams_Past;

    /// <summary>"in 20 days", "tomorrow", "today", "yesterday", "17 days ago".</summary>
    public string WhenText => (exam.Date.DayNumber - today.DayNumber) switch
    {
        0 => Strings.Row_Today,
        1 => Strings.Exams_Tomorrow,
        -1 => Strings.Row_Yesterday,
        > 1 and var days => string.Format(Strings.Exams_InDays, days),
        var days => string.Format(Strings.Row_DaysAgo, -days),
    };

    /// <summary>"6 of 8 accepted": who was invited on Spond and said yes.</summary>
    public string PeopleText => string.Format(Strings.Exams_Accepted, exam.AttendedCount, exam.InvitedCount);

    /// <summary>What matters about the exam beyond that: the members saved for it while it is to come, the belts handed out once it has been.</summary>
    public string ExtraText => IsUpcoming
        ? string.Format(Strings.Exams_SavedCount, exam.PreSavedCount)
        : string.Format(Strings.Exams_BeltsCount, exam.BeltsAwardedCount);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// What the suggestions can be based on: the default (previous exam to this exam) or a filter saved on the statistics page. Re-reads its
/// label after <see cref="Refresh"/>; the entries are not replaced when the language changes (a replaced list loses its selection in WPF).
/// </summary>
public sealed class BasisOption(SavedFilterDto? filter = null, ExamDto? exam = null, bool ofThisExam = true) : ObservableObject
{
    /// <summary>The saved filter; <c>null</c> = the default.</summary>
    public int? FilterId => filter?.Id;

    public bool IsDefault => filter is null;

    /// <summary>The saved filter was saved for the exam that is planned, not for another one.</summary>
    public bool IsOfThisExam => filter is not null && ofThisExam;

    public SavedFilterDto? Filter => filter;

    /// <summary>The filter's name, with the exam it was saved for when that is another exam than the one planned.</summary>
    public string Label => filter is null
        ? Strings.Plan_BasisDefault
        : ofThisExam || exam is null ? filter.Name : string.Format(Strings.Plan_BasisOfExam, filter.Name, exam.Name, Display.Date(exam.Date));

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Which of the suggested members the list shows.</summary>
public enum SuggestionFilter
{
    Fit,
    NeedsData,
    NotYet,
    NoNextBelt,
    All,
}

/// <summary>One choice of the list filter, with how many members it shows. Re-reads its text after <see cref="Refresh"/>.</summary>
public sealed class SuggestionFilterOption(SuggestionFilter kind) : ObservableObject
{
    private int _count;

    public SuggestionFilter Kind => kind;

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(Label));
        }
    }

    public string Name => kind switch
    {
        SuggestionFilter.Fit => Strings.Plan_FilterFit,
        SuggestionFilter.NeedsData => Strings.Plan_FilterNeedsData,
        SuggestionFilter.NotYet => Strings.Plan_FilterNotYet,
        SuggestionFilter.NoNextBelt => Strings.Plan_FilterNoNext,
        _ => Strings.Plan_FilterAll,
    };

    /// <summary>"Fit 4".</summary>
    public string Label => $"{Name} {Count}";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// One member in the suggestions for an exam: the facts, whether they are fit and why not, and whether they are ticked to be saved for
/// the exam. The ticks are the user's to change; <see cref="IsSaved"/> is what the server holds.
/// </summary>
public sealed partial class SuggestionRow : ObservableObject
{
    private readonly ExamSuggestionDto _dto;
    private readonly AttendanceFilter _filter;

    public SuggestionRow(ExamSuggestionDto dto, BeltOption belt, BeltOption? nextBelt, AttendanceFilter filter)
    {
        _dto = dto;
        _filter = filter;
        Belt = belt;
        NextBelt = nextBelt;
        _isSaved = _isSelected = dto.IsPreSaved;
    }

    public ExamSuggestionDto Dto => _dto;

    public int MemberId => _dto.MemberId;

    public string Name => _dto.Name;

    public string SubGroupsText => string.Join(", ", _dto.SubGroups);

    // --- Belts -------------------------------------------------------------------------------------------

    /// <summary>The belt the member holds; the empty entry when none is assigned.</summary>
    public BeltOption Belt { get; }

    public bool HasBelt => !Belt.IsNone;

    /// <summary>The belt they would take; <c>null</c> when there is none (nothing above their belt, or no belt known).</summary>
    public BeltOption? NextBelt { get; }

    public bool HasNextBelt => NextBelt is not null;

    // --- What the next belt asks for ---------------------------------------------------------------------------

    /// <summary>"11 (min. 8)", "? (min. 8)" without a birth date, "11" when nothing is asked, "–" when there is no next belt.</summary>
    public string AgeText => !HasNextBelt ? Display.None : Cell(_dto.AgeYears?.ToString(), _dto.MinAgeYears);

    public CheckState AgeState => HasNextBelt ? _dto.AgeCheck : CheckState.Met;

    /// <summary>"7 mo. (min. 3)": the full months since the belt was received (or since the last exam), counted until the exam day.</summary>
    public string WaitText => !HasNextBelt
        ? Display.None
        : Cell(_dto.WaitedMonths is { } months ? string.Format(Strings.Plan_Months, months) : null, _dto.MinWaitMonths);

    public CheckState WaitState => HasNextBelt ? _dto.WaitCheck : CheckState.Met;

    /// <summary>Where the waiting time counts from: the day the belt was received, or, without it, the last exam, which says so.</summary>
    public string WaitTip => _dto.WaitingSince is not { } since
        ? Strings.Plan_WaitFromUnknown
        : _dto.BeltSince is not null
            ? string.Format(Strings.Plan_WaitFromBelt, Display.Date(since))
            : string.Format(Strings.Plan_WaitFromExam, Display.Date(since));

    private static string Cell(string? value, int minimum) => (value, minimum) switch
    {
        (null, 0) => Display.None,
        (null, _) => string.Format(Strings.Plan_CellMin, Display.Unknown, minimum),
        (_, 0) => value,
        _ => string.Format(Strings.Plan_CellMin, value, minimum),
    };

    // --- Evaluation -----------------------------------------------------------------------------------------

    /// <summary>"3.4 (min. 3)", "? (min. 3)" without an event to average over, "3.4" when nothing is asked, "–" when there is no next belt.</summary>
    public string StarsText => !HasNextBelt ? Display.None : Cell(_dto.AverageStars is { } stars ? Display.Stars(stars) : null, _dto.MinAverageStars);

    public CheckState StarsState => HasNextBelt ? _dto.RatingCheck : CheckState.Met;

    private static string Cell(string? value, double minimum) => (value, minimum) switch
    {
        (null, <= 0) => Display.None,
        (null, _) => string.Format(Strings.Plan_CellMin, Display.Unknown, Display.Stars(minimum)),
        (_, <= 0) => value,
        _ => string.Format(Strings.Plan_CellMin, value, Display.Stars(minimum)),
    };

    // --- Attendance -----------------------------------------------------------------------------------------

    public string AttendanceText => _dto.Invited == 0 ? Display.None : Display.Percent(_dto.AttendanceRate);

    public double AttendanceFraction => _dto.AttendanceRate / 100.0;

    public string AttendedOfInvitedText => $"{_dto.Attended} / {_dto.Invited}";

    /// <summary>What a screen reader says for the attendance bar.</summary>
    public string AttendanceAriaText => string.Format(Strings.Rate_Aria, AttendanceText, AttendedOfInvitedText);

    public QualificationStatus AttendanceStatus => _dto.AttendanceStatus;

    /// <summary>The same as <see cref="AttendanceStatus"/>, under the name the attendance bar style of the statistics page binds to.</summary>
    public QualificationStatus Status => _dto.AttendanceStatus;

    public bool AttendanceMet => _dto.AttendanceStatus == QualificationStatus.Qualified;

    public bool HasAttendance => _dto.Invited > 0;

    public int EngagementScore => _dto.EngagementScore;

    public string LastExamText => _dto.LastExam is { } day ? Display.Date(day) : Display.None;

    // --- The verdict ------------------------------------------------------------------------------------

    public SuggestionVerdict Verdict => _dto.Verdict;

    public bool IsFit => _dto.Verdict == SuggestionVerdict.Fit;

    public string VerdictText => Display.Verdict(_dto.Verdict);

    public string VerdictTip => Display.VerdictTip(_dto.Verdict);

    /// <summary>
    /// What is missed or not known, in words ("Waited 1 of 3 months …"), or, for a member who is fit, what they are fit for. Empty
    /// for somebody with nothing to take.
    /// </summary>
    public string ReasonText
    {
        get
        {
            if (_dto.Verdict == SuggestionVerdict.Fit) return string.Format(Strings.Plan_ReasonFit, NextBelt?.Label);
            if (_dto.BeltId is null) return Strings.Plan_ReasonNoBelt;
            if (_dto.Verdict == SuggestionVerdict.NoNextBelt) return Strings.Plan_ReasonTop;

            var reasons = new List<string>();
            switch (_dto.AttendanceStatus)
            {
                case QualificationStatus.BelowThreshold:
                    reasons.Add(string.Format(Strings.Plan_ReasonAttendance,
                        Display.Percent(_dto.AttendanceRate), _dto.Attended, _dto.Invited, Display.Percent(_filter.MinAttendancePercent)));
                    break;
                case QualificationStatus.InsufficientData:
                    reasons.Add(_dto.Invited == 0
                        ? Strings.Plan_ReasonNoEvents
                        : string.Format(Strings.Plan_ReasonFewEvents, _dto.Invited, Math.Max(1, _filter.MinInvitedEvents)));
                    break;
            }

            if (_dto.RatingCheck == CheckState.NotMet)
                reasons.Add(_dto.AverageStars is { } average
                    ? string.Format(Strings.Plan_ReasonStars, Display.Stars(average), Display.Stars(_dto.MinAverageStars))
                    : string.Format(Strings.Plan_ReasonNoStars, Display.Stars(_dto.MinAverageStars)));
            if (_dto.AgeCheck == CheckState.NotMet) reasons.Add(string.Format(Strings.Plan_ReasonAge, _dto.AgeYears, _dto.MinAgeYears));
            if (_dto.AgeCheck == CheckState.Unknown) reasons.Add(string.Format(Strings.Plan_ReasonAgeUnknown, _dto.MinAgeYears));
            if (_dto.WaitCheck == CheckState.NotMet) reasons.Add(string.Format(Strings.Plan_ReasonWait, _dto.WaitedMonths, _dto.MinWaitMonths));
            if (_dto.WaitCheck == CheckState.Unknown) reasons.Add(Strings.Plan_ReasonWaitUnknown);

            return string.Join(' ', reasons);
        }
    }

    // --- Spond -----------------------------------------------------------------------------------------

    /// <summary>"Not invited", "Accepted", "Declined", "No answer yet", "Waiting list".</summary>
    public string InvitationText => Display.Invitation(_dto.Invitation);

    public bool IsInvited => _dto.Invitation is not null;

    // --- Saving for the exam -------------------------------------------------------------------------------

    /// <summary>Ticked to be saved for the exam. Starts as what the server holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChange))]
    private bool _isSelected;

    /// <summary>The member is saved for the exam, as far as the server said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChange))]
    private bool _isSaved;

    /// <summary>The tick differs from what the server holds: saving would change something.</summary>
    public bool HasChange => IsSelected != IsSaved;

    public void Refresh()
    {
        Belt.Refresh();
        NextBelt?.Refresh();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// One member who took part in an exam (accepted, or got a belt at it) and the belt they received. Choosing another belt records it
/// at once; <see cref="Reset"/> takes what the server holds back, e.g. after the server refused.
/// </summary>
public sealed partial class AttendeeRow : ObservableObject
{
    private readonly Func<AttendeeRow, BeltOption?, Task> _chosen;
    private bool _quiet;

    public AttendeeRow(ExamMemberDto member, BeltOption received, Func<AttendeeRow, BeltOption?, Task> chosen)
    {
        Member = member;
        _chosen = chosen;
        _selectedBelt = received;
    }

    /// <summary>What the server holds about the member and the exam.</summary>
    public ExamMemberDto Member { get; private set; }

    public int MemberId => Member.MemberId;

    public string Name => Member.Name;

    /// <summary>What the member answered: "Accepted", or something else for somebody who got a belt without accepting.</summary>
    public string AnswerText => Display.Invitation(Member.Invitation);

    /// <summary>The belt received; the empty entry for none. Choosing one records it.</summary>
    [ObservableProperty]
    private BeltOption? _selectedBelt;

    partial void OnSelectedBeltChanged(BeltOption? value)
    {
        if (!_quiet) _ = _chosen(this, value);
    }

    /// <summary>Counts how often the row was set to what the server holds; a page that cannot tell a select went back (Blazor) keys it by this.</summary>
    public int Revision { get; private set; }

    /// <summary>Shows what the server holds without recording anything.</summary>
    public void Reset(ExamMemberDto member, BeltOption received)
    {
        Member = member;
        Revision++;
        _quiet = true;
        try
        {
            SelectedBelt = received;
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(nameof(Member));
        OnPropertyChanged(nameof(AnswerText));
    }

    public void Refresh()
    {
        SelectedBelt?.Refresh();
        OnPropertyChanged(nameof(AnswerText));
    }
}

/// <summary>
/// One member's payment for the exam on show: what is due (general fee, stamp fee, belt price), and the fields an administrator fills in
/// (what was paid, a note, whether the member got the belt elsewhere). The fields are only saved when asked to; <see cref="Reset"/> shows what the server holds.
/// </summary>
public sealed partial class PaymentRow : ObservableObject
{
    private readonly Func<PaymentRow, Task> _save;
    private readonly Func<bool> _canEdit;

    public PaymentRow(ExamPaymentDto payment, BeltOption belt, Func<bool> canEdit, Func<PaymentRow, Task> save)
    {
        Payment = payment;
        Belt = belt;
        _canEdit = canEdit;
        _save = save;
        Paid = new MoneyField(payment.Paid);
        _note = payment.Note ?? "";
        _ownBelt = payment.OwnBelt;
        Paid.PropertyChanged += (_, _) => NotifyEdited();
    }

    /// <summary>What the server holds.</summary>
    public ExamPaymentDto Payment { get; private set; }

    public int MemberId => Payment.MemberId;

    public string Name => Payment.Name;

    /// <summary>The belt the member receives (recorded, or the next one); the empty entry when it is not known.</summary>
    public BeltOption Belt { get; }

    public string DueText => Display.Money(Payment.Due);

    public string GeneralFeeText => Display.Money(Payment.GeneralFee);

    public string StampFeeText => Display.Money(Payment.StampFee);

    /// <summary>The price of the belt as it is charged: 0 for a member with their own belt.</summary>
    public string BeltPriceText => Display.Money(Payment.BeltPrice);

    /// <summary>The member got the belt elsewhere (e.g. handed down by a sibling), so its price is not charged. Takes effect when saved.</summary>
    [ObservableProperty]
    private bool _ownBelt;

    partial void OnOwnBeltChanged(bool value) => NotifyEdited();

    /// <summary>What was paid, as typed.</summary>
    public MoneyField Paid { get; }

    [ObservableProperty]
    private string _note;

    partial void OnNoteChanged(string value) => NotifyEdited();

    /// <summary>What the server holds: not paid, partly paid or paid. Follows the saved payment, not what is typed.</summary>
    public PaymentStatus Status => Payment.Status;

    public string StatusText => Status switch
    {
        PaymentStatus.Paid => Strings.Pay_StatusPaid,
        PaymentStatus.Partial => Strings.Pay_StatusPartial,
        _ => Strings.Pay_StatusOpen,
    };

    /// <summary>"Paid on 26 Sep 2026", or empty when nothing was paid.</summary>
    public string PaidOnText => Payment.PaidOn is { } day ? string.Format(Strings.Pay_PaidOn, Display.Date(day)) : "";

    /// <summary>The fields differ from what the server holds.</summary>
    public bool HasChange => Paid.HasChange || Note.Trim() != (Payment.Note ?? "") || OwnBelt != Payment.OwnBelt;

    public bool CanEdit => _canEdit();

    public bool CanSave => CanEdit && HasChange && Paid.IsValid;

    /// <summary>Paying what is due would change something: less than that is paid. Not while the own belt is changed, which changes what is due once saved.</summary>
    public bool CanMarkPaid => CanEdit && Paid.Amount < Payment.Due && OwnBelt == Payment.OwnBelt;

    /// <summary>Counts how often the row was set to what the server holds; a page that cannot tell it went back keys by this.</summary>
    public int Revision { get; private set; }

    private void NotifyEdited()
    {
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanMarkPaid));
        SaveCommand.NotifyCanExecuteChanged();
        MarkPaidCommand.NotifyCanExecuteChanged();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => _save(this);

    /// <summary>Fills in the amount that is due and saves it.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanMarkPaid))]
    private Task MarkPaidAsync()
    {
        Paid.Text = Display.MoneyInput(Payment.Due);
        return _save(this);
    }

    /// <summary>Shows what the server holds.</summary>
    public void Reset(ExamPaymentDto payment)
    {
        Payment = payment;
        Revision++;
        Paid.Reset(payment.Paid);
        Note = payment.Note ?? "";
        OwnBelt = payment.OwnBelt;
        OnPropertyChanged(string.Empty);
        NotifyEdited();
    }

    /// <summary>The permissions changed: what depends on them is announced again.</summary>
    public void PermissionsChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        NotifyEdited();
    }

    public void Refresh()
    {
        Belt.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PaidOnText));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(GeneralFeeText));
        OnPropertyChanged(nameof(StampFeeText));
        OnPropertyChanged(nameof(BeltPriceText));
    }
}
