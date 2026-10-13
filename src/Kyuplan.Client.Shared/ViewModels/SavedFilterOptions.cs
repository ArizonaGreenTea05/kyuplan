using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// An exam in the lists of the saved-filter section: a Spond event that is a belt exam. The entry without an exam
/// (<see cref="IsNew"/>) stands for "a new exam" in the list the save form offers. Like <see cref="EventOption"/> it reads its texts again after
/// <see cref="Refresh"/>, so that the lists survive a change of language (a replaced list loses its selection in WPF).
/// </summary>
public sealed class ExamOption : ObservableObject
{
    private readonly ExamDto? _exam;

    public ExamOption(ExamDto? exam = null)
    {
        _exam = exam;
        Filters = exam?.Filters.Select(f => new SavedFilterOption(exam, f)).ToList() ?? [];
    }

    /// <summary>True for the entry that stands for an exam that does not exist yet.</summary>
    public bool IsNew => _exam is null;

    /// <summary>The server's id; 0 for the "new exam" entry.</summary>
    public int Id => _exam?.Id ?? 0;

    /// <summary>The Spond event that is the exam; empty for the "new exam" entry.</summary>
    public string EventId => _exam?.EventId ?? EventOption.NoneId;

    /// <summary>The event's name.</summary>
    public string Name => _exam?.Name ?? "";

    /// <summary>The day of the event.</summary>
    public DateOnly? Date => _exam?.Date;

    /// <summary>"Gürtelprüfung · 26 Sep 2026", or the prompt of the "new exam" entry.</summary>
    public string Label => _exam is null ? Strings.Saved_NewExam : $"{_exam.Name} · {Display.Date(_exam.Date)}";

    /// <summary>The filters saved for this exam, by name.</summary>
    public IReadOnlyList<SavedFilterOption> Filters { get; }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var filter in Filters) filter.Refresh();
    }
}

/// <summary>A saved filter, listed with the exam it belongs to.</summary>
public sealed class SavedFilterOption(ExamDto exam, SavedFilterDto filter) : ObservableObject
{
    public int Id => filter.Id;

    public int ExamId => exam.Id;

    public string Name => filter.Name;

    /// <summary>"Gürtelprüfung · 26 Sep 2026": the exam the filter was saved for.</summary>
    public string ExamText => $"{exam.Name} · {Display.Date(exam.Date)}";

    public FilterSettingsDto Settings => filter.Settings;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
