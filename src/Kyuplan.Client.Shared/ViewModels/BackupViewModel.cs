using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One line of the list of backup databases, with ready-to-show texts that follow the language after <see cref="Refresh"/>.</summary>
public sealed class BackupTargetRow(BackupTargetDto target) : ObservableObject
{
    public BackupTargetDto Target => target;

    public int Id => target.Id;

    public string Name => target.Name;

    public string Location => target.Location;

    public bool Enabled => target.Enabled;

    public string KindText => BackupViewModel.KindLabel(target.Kind);

    /// <summary>"every 6 h".</summary>
    public string IntervalText => BackupViewModel.IntervalLabel(target.IntervalMinutes);

    public bool HasFailed => target.LastSucceeded == false;

    /// <summary>"Has not run yet", "Last update Sat, 26 Sep · 10:00" or "Failed Sat, 26 Sep · 10:00: reason".</summary>
    public string StatusText => target.LastRunUtc is not { } last
        ? Strings.Backup_NeverRan
        : target.LastSucceeded == true
            ? string.Format(Strings.Backup_LastOk, Display.EventStart(last.ToLocalTime()))
            : string.Format(Strings.Backup_LastFailed, Display.EventStart(last.ToLocalTime()), target.LastMessage);

    /// <summary>"Next update Sat, 26 Sep · 10:00", or "Switched off".</summary>
    public string NextText => target.NextRunUtc is { } next ? string.Format(Strings.Backup_Next, Display.EventStart(next.ToLocalTime())) : Strings.Backup_Off;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A choice of the interval or the kind of a backup database.</summary>
public sealed record BackupChoice<T>(T Value, string Label);

/// <summary>
/// The backup page: the backup databases (second databases that get a complete copy of all data at an interval), the download of a backup file and the
/// replacement of all data with an uploaded one. The page asks the user to confirm before anything is deleted or replaced. The clients save the downloaded file
/// (<see cref="DownloadReady"/>) and pick the file to restore (<see cref="StageRestore"/>) with what their platform offers. Used by both clients.
/// </summary>
public sealed partial class BackupViewModel : PageViewModel
{
    /// <summary>The intervals offered; the one a target has already is added to the list when it is edited.</summary>
    private static readonly int[] PresetMinutes = [15, 60, 360, 720, 1440, 10080];

    private readonly IPlannerApi _api;
    private readonly ApiRestartWatcher _watcher;
    private Func<string>? _notice;
    private Func<Stream>? _restoreFile;

    public BackupViewModel(ShellViewModel shell, IPlannerApi api, ApiRestartWatcher? watcher = null) : base(shell)
    {
        _api = api;
        _watcher = watcher ?? new ApiRestartWatcher(api);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(IsRestarting)) NotifyStateChanged();
        };
    }

    public override string Title => Strings.Nav_Backup;

    protected override string? ViewPermission => Permission.BackupsView;

    /// <summary>The page is in the middle of something (a form, a question): its lists must not be replaced under it.</summary>
    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => !IsFormOpen && !IsConfirmingDelete && !IsConfirmingRestore;

    public bool CanEdit => Shell.Can(Permission.BackupsEdit);

    public bool CanDownload => Shell.Can(Permission.BackupsDownload);

    public bool CanRestore => Shell.Can(Permission.BackupsRestore);

    protected override void OnPermissionsChanged()
    {
        if (!CanView) Reset();

        base.OnPermissionsChanged();
        NotifyStateChanged();
    }

    private void Reset()
    {
        Targets = [];
        HasLoaded = false;
        CloseForm();
        PendingDelete = null;
        CancelRestore();
        SetNotice(null);
    }

    // --- The list ----------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private IReadOnlyList<BackupTargetRow> _targets = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    public bool IsEmpty => HasLoaded && Targets.Count == 0;

    public string EmptyText => CanEdit ? Strings.Backup_Empty : Strings.Backup_EmptyView;

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync() => await LoadAsync(async ct =>
    {
        Targets = Rows(await _api.GetBackupTargetsAsync(ct));
        HasLoaded = true;
    });

    private static IReadOnlyList<BackupTargetRow> Rows(IEnumerable<BackupTargetDto> targets) =>
        [.. targets.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new BackupTargetRow(t))];

    // --- Result of the last action -------------------------------------------------------------------------

    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- Texts ---------------------------------------------------------------------------------------------

    public static string KindLabel(BackupTargetKind kind) => kind == BackupTargetKind.Sqlite ? Strings.Backup_KindSqlite : Strings.Backup_KindPostgres;

    /// <summary>"every 15 min", "every 6 h", "every 1 d".</summary>
    public static string IntervalLabel(int minutes) =>
        minutes % 1440 == 0 ? string.Format(Strings.Backup_EveryDays, minutes / 1440)
        : minutes % 60 == 0 ? string.Format(Strings.Backup_EveryHours, minutes / 60)
        : string.Format(Strings.Backup_EveryMinutes, minutes);

    public IReadOnlyList<BackupChoice<BackupTargetKind>> Kinds =>
        [new(BackupTargetKind.Sqlite, KindLabel(BackupTargetKind.Sqlite)), new(BackupTargetKind.PostgreSql, KindLabel(BackupTargetKind.PostgreSql))];

    /// <summary>The intervals to choose from (the target's own is among them when it is edited).</summary>
    public IReadOnlyList<BackupChoice<int>> Intervals =>
        [.. PresetMinutes.Append(FormIntervalMinutes).Distinct().Order().Select(m => new BackupChoice<int>(m, IntervalLabel(m)))];

    // --- The form ------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    private bool _isFormOpen;

    /// <summary>The target the form changes; <c>null</c> while it creates one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(IsCreating))]
    private BackupTargetRow? _editing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _formName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocationLabel), nameof(LocationHint), nameof(CanSave))]
    private BackupTargetKind _formKind = BackupTargetKind.Sqlite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _formLocation = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Intervals), nameof(CanSave))]
    private int _formIntervalMinutes = 1440;

    [ObservableProperty]
    private bool _formEnabled = true;

    public bool IsCreating => Editing is null;

    public string FormTitle => Editing is null ? Strings.Backup_FormNew : Strings.Backup_FormEdit;

    public string LocationLabel => FormKind == BackupTargetKind.Sqlite ? Strings.Backup_LocationSqlite : Strings.Backup_LocationPostgres;

    public string LocationHint => FormKind == BackupTargetKind.Sqlite ? Strings.Backup_LocationHintSqlite : Strings.Backup_LocationHintPostgres;

    private BackupTargetInputDto Input() => new(FormName, FormKind, FormLocation, FormIntervalMinutes, FormEnabled);

    public bool CanSave => CanEdit && IsFormOpen && !IsBusy && BackupRules.Check(Input()) is null;

    private void CloseForm()
    {
        IsFormOpen = false;
        Editing = null;
    }

    private bool CanBeginCreate() => CanEdit && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        if (!CanBeginCreate()) return; // Execute does not ask CanExecute

        Prepare();
        Editing = null;
        FormName = "";
        FormKind = BackupTargetKind.Sqlite;
        FormLocation = "";
        FormIntervalMinutes = 1440;
        FormEnabled = true;
        IsFormOpen = true;
    }

    private bool CanBeginEdit(BackupTargetRow? row) => CanEdit && row is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private void BeginEdit(BackupTargetRow row)
    {
        if (!CanBeginEdit(row)) return;

        Prepare();
        Editing = row;
        FormName = row.Name;
        FormKind = row.Target.Kind;
        FormLocation = row.Location;
        FormIntervalMinutes = row.Target.IntervalMinutes;
        FormEnabled = row.Enabled;
        IsFormOpen = true;
    }

    private void Prepare()
    {
        ErrorMessage = null;
        SetNotice(null);
        PendingDelete = null;
        CancelRestore();
    }

    [RelayCommand]
    private void CancelForm() => CloseForm();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var input = Input();
        var editing = Editing;
        SetNotice(null);
        var saved = false;
        await LoadAsync(async ct =>
        {
            if (editing is null) await _api.CreateBackupTargetAsync(input, ct);
            else await _api.UpdateBackupTargetAsync(editing.Id, input, ct);
            saved = true;
            Targets = Rows(await _api.GetBackupTargetsAsync(ct));
        });
        if (!saved) return;

        CloseForm();
        var name = input.Name.Trim();
        SetNotice(() => string.Format(editing is null ? Strings.Backup_Created : Strings.Backup_Saved, name));
    }

    // --- Updating and deleting a backup database -------------------------------------------------------------

    private bool CanRun(BackupTargetRow? row) => CanEdit && row is not null && !IsBusy && !IsRestarting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunNowAsync(BackupTargetRow row)
    {
        if (!CanRun(row)) return;

        Prepare();
        BackupTargetDto? result = null;
        await LoadAsync(async ct =>
        {
            result = await _api.RunBackupTargetAsync(row.Id, ct);
            Targets = Rows(await _api.GetBackupTargetsAsync(ct));
        });
        if (result is null) return;

        var name = result.Name;
        var reason = result.LastMessage;
        SetNotice(() => result.LastSucceeded == true ? string.Format(Strings.Backup_RunOk, name) : string.Format(Strings.Backup_RunFailed, name, reason));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmDeleteText))]
    private BackupTargetRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    public string ConfirmDeleteText => PendingDelete is { } row ? string.Format(Strings.Backup_ConfirmDelete, row.Name) : "";

    partial void OnPendingDeleteChanged(BackupTargetRow? value) => NotifyStateChanged();

    private bool CanAskDelete(BackupTargetRow? row) => CanEdit && row is not null && !ReferenceEquals(row, PendingDelete) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAskDelete))]
    private void AskDelete(BackupTargetRow row)
    {
        if (!CanAskDelete(row)) return;

        Prepare();
        PendingDelete = row;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } row || !CanEdit) return;

        PendingDelete = null;
        var deleted = false;
        await LoadAsync(async ct =>
        {
            await _api.DeleteBackupTargetAsync(row.Id, ct);
            deleted = true;
            Targets = Rows(await _api.GetBackupTargetsAsync(ct));
        });

        if (!deleted) return;

        if (Editing?.Id == row.Id) CloseForm();
        var name = row.Name;
        SetNotice(() => string.Format(Strings.Backup_Deleted, name));
    }

    // --- The backup file: download ---------------------------------------------------------------------------

    /// <summary>The API sent a backup file; the client saves it where the user says.</summary>
    public event EventHandler<BackupFile>? DownloadReady;

    private bool CanStartDownload() => CanDownload && !IsBusy && !IsRestarting;

    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private async Task DownloadAsync()
    {
        if (!CanStartDownload()) return;

        Prepare();
        BackupFile? file = null;
        await LoadAsync(async ct => file = await _api.DownloadBackupAsync(ct));
        if (file is null) return;

        DownloadReady?.Invoke(this, file);
        SetNotice(() => Strings.Backup_Downloaded);
    }

    // --- The backup file: restore ----------------------------------------------------------------------------

    /// <summary>The name of the file that is waiting for the confirmation that replaces all data with it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRestore), nameof(ConfirmRestoreText))]
    private string? _pendingRestoreName;

    [ObservableProperty]
    private bool _isRestarting;

    public bool IsConfirmingRestore => PendingRestoreName is not null;

    public string ConfirmRestoreText => PendingRestoreName is { } name ? string.Format(Strings.Backup_ConfirmRestore, name) : "";

    partial void OnPendingRestoreNameChanged(string? value) => NotifyStateChanged();

    /// <summary>The user picked a file to restore from; nothing happens before they confirm. <paramref name="open"/> opens the file again for the upload.</summary>
    public void StageRestore(string fileName, Func<Stream> open)
    {
        if (!CanRestore || IsBusy || IsRestarting) return;

        Prepare();
        _restoreFile = open;
        PendingRestoreName = fileName;
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _restoreFile = null;
        PendingRestoreName = null;
    }

    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (_restoreFile is not { } open || !CanRestore || IsBusy) return;

        CancelRestore();
        BackupRestoredDto? restored = null;
        await LoadAsync(async ct =>
        {
            await using var file = open();
            restored = await _api.RestoreBackupAsync(file, ct);
        });
        if (restored is null) return; // the API refused the file, and nothing was changed: the error says why

        IsRestarting = true;
        var rows = restored.Rows;
        SetNotice(() => string.Format(Strings.Backup_RestoredRestarting, rows));
        var back = await _watcher.WaitUntilBackAsync(CancellationToken.None);
        IsRestarting = false;
        if (!back)
        {
            SetNotice(null);
            ErrorMessage = Strings.Setup_NotBack;
            return;
        }

        SetNotice(() => Strings.Backup_Restored);
        await ReloadAsync();
    }

    private void NotifyStateChanged()
    {
        BeginCreateCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var row in Targets) row.Refresh();
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(LocationHint));
        OnPropertyChanged(nameof(Intervals));
        OnPropertyChanged(nameof(Kinds));
        OnPropertyChanged(nameof(ConfirmDeleteText));
        OnPropertyChanged(nameof(ConfirmRestoreText));
    }
}
