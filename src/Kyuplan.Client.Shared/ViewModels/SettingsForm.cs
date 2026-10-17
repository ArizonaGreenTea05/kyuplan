using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The settings as they are typed, on the setup screen and on the settings page: the same fields, checked with the same rules
/// (<see cref="SettingsRules"/>) as the API checks them. The time zone is only checked by the API, which knows the zones of its system.
/// </summary>
public sealed partial class SettingsForm : ObservableObject
{
    public SettingsForm() => BackupAccounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(BackupAccounts));

    /// <summary>More Spond logins, tried in this order after the first one.</summary>
    public ObservableCollection<BackupAccountForm> BackupAccounts { get; } = [];

    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _accountsDatabasePath = "";
    [ObservableProperty] private bool _useDemoData;
    [ObservableProperty] private string _spondEmail = "";
    [ObservableProperty] private string _spondPhoneNumber = "";
    [ObservableProperty] private string _spondPassword = "";
    [ObservableProperty] private bool _hasSpondPassword;
    [ObservableProperty] private bool _ignoreAdministrators;
    [ObservableProperty] private string _timeZone = "";
    [ObservableProperty] private string _examKeyword = "";
    [ObservableProperty] private int _defaultMinAttendancePercent;
    [ObservableProperty] private int _defaultMinInvitedEvents;
    [ObservableProperty] private double _defaultEventStars = RatingRules.StandardDefaultEventStars;
    [ObservableProperty] private double _examMinAverageStars = RatingRules.StandardExamMinAverageStars;
    [ObservableProperty] private bool _syncEnabled;
    [ObservableProperty] private int _syncIntervalMinutes;
    [ObservableProperty] private string _allowedHosts = SettingsRules.AnyHost;

    public void Load(SettingsDto settings)
    {
        DatabasePath = settings.DatabasePath;
        AccountsDatabasePath = settings.AccountsDatabasePath;
        UseDemoData = settings.UseDemoData;
        SpondEmail = settings.SpondEmail ?? "";
        SpondPhoneNumber = settings.SpondPhoneNumber ?? "";
        SpondPassword = "";
        HasSpondPassword = settings.HasSpondPassword;
        IgnoreAdministrators = settings.IgnoreAdministrators;
        TimeZone = settings.TimeZone;
        ExamKeyword = settings.ExamKeyword;
        DefaultMinAttendancePercent = settings.DefaultMinAttendancePercent;
        DefaultMinInvitedEvents = settings.DefaultMinInvitedEvents;
        DefaultEventStars = settings.DefaultEventStars;
        ExamMinAverageStars = settings.ExamMinAverageStars;
        SyncEnabled = settings.SyncEnabled;
        SyncIntervalMinutes = settings.SyncIntervalMinutes;
        AllowedHosts = string.IsNullOrWhiteSpace(settings.AllowedHosts) ? SettingsRules.AnyHost : settings.AllowedHosts;
        BackupAccounts.Clear();
        foreach (var account in settings.BackupAccounts ?? []) AddBackup(new BackupAccountForm(account));
        OnPropertyChanged(string.Empty);
    }

    public SettingsInputDto ToInput() => new(
        DatabasePath.Trim(), AccountsDatabasePath.Trim(), UseDemoData, SpondEmail.Trim(), SpondPhoneNumber.Trim(),
        SpondPassword.Length == 0 ? null : SpondPassword, IgnoreAdministrators, TimeZone.Trim(), ExamKeyword.Trim(),
        DefaultMinAttendancePercent, DefaultMinInvitedEvents, SyncEnabled, SyncIntervalMinutes,
        [.. BackupAccounts.Where(a => !a.IsEmpty).Select(a => a.ToInput())], AllowedHosts.Trim(),
        DefaultEventStars, ExamMinAverageStars);

    [RelayCommand]
    private void AddBackupAccount() => AddBackup(new BackupAccountForm());

    [RelayCommand]
    private void RemoveBackupAccount(BackupAccountForm account) => BackupAccounts.Remove(account);

    private void AddBackup(BackupAccountForm account)
    {
        account.PropertyChanged += (_, _) => OnPropertyChanged(nameof(BackupAccounts));
        BackupAccounts.Add(account);
    }

    /// <summary>Everything typed is what the API runs with (and no new Spond password was typed).</summary>
    public bool Matches(SettingsDto s) =>
        DatabasePath.Trim() == s.DatabasePath && AccountsDatabasePath.Trim() == s.AccountsDatabasePath && UseDemoData == s.UseDemoData &&
        SpondEmail.Trim() == (s.SpondEmail ?? "") && SpondPhoneNumber.Trim() == (s.SpondPhoneNumber ?? "") && SpondPassword.Length == 0 &&
        IgnoreAdministrators == s.IgnoreAdministrators && TimeZone.Trim() == s.TimeZone && ExamKeyword.Trim() == s.ExamKeyword &&
        DefaultMinAttendancePercent == s.DefaultMinAttendancePercent && DefaultMinInvitedEvents == s.DefaultMinInvitedEvents &&
        DefaultEventStars == s.DefaultEventStars && ExamMinAverageStars == s.ExamMinAverageStars &&
        SyncEnabled == s.SyncEnabled && SyncIntervalMinutes == s.SyncIntervalMinutes && AllowedHosts.Trim() == (s.AllowedHosts ?? SettingsRules.AnyHost) && BackupMatches(s.BackupAccounts ?? []);

    private bool BackupMatches(IReadOnlyList<SpondAccountDto> saved)
    {
        var typed = BackupAccounts.Where(a => !a.IsEmpty).ToList();
        return typed.Count == saved.Count && typed.Zip(saved).All(p => p.First.Matches(p.Second));
    }

    public string? DatabasePathProblem => SettingsRules.IsPath(DatabasePath) ? null : Strings.Settings_PathInvalid;

    public string? AccountsDatabasePathProblem =>
        !SettingsRules.IsPath(AccountsDatabasePath) ? Strings.Settings_PathInvalid
        : SettingsRules.AreSameFile(DatabasePath, AccountsDatabasePath) ? Strings.Settings_DatabasesSame
        : null;

    public string? SpondProblem =>
        !UseDemoData && SettingsRules.HasHalfCredentials(SpondEmail, SpondPhoneNumber, SpondPassword.Length > 0 || HasSpondPassword)
            ? Strings.Settings_SpondIncomplete
            : null;

    public string? BackupProblem =>
        !UseDemoData && BackupAccounts.Any(a => a.IsIncomplete) ? Strings.Settings_BackupIncomplete : null;

    public string? TimeZoneProblem => string.IsNullOrWhiteSpace(TimeZone) ? Strings.Settings_TimeZoneInvalid : null;

    public string? PercentProblem => SettingsRules.IsPercent(DefaultMinAttendancePercent) ? null : Strings.Settings_PercentInvalid;

    public string? InvitedEventsProblem => SettingsRules.IsInvitedEvents(DefaultMinInvitedEvents) ? null : Strings.Settings_InvitedInvalid;

    public string? DefaultEventStarsProblem => SettingsRules.IsStars(DefaultEventStars) ? null : Strings.Settings_StarsInvalid;

    public string? ExamMinAverageStarsProblem => SettingsRules.IsStars(ExamMinAverageStars) ? null : Strings.Settings_StarsInvalid;

    public string? SyncIntervalProblem =>
        SettingsRules.IsSyncInterval(SyncIntervalMinutes) ? null : string.Format(Strings.Settings_SyncIntervalInvalid, SettingsRules.MaxSyncIntervalMinutes);

    public string? AllowedHostsProblem => SettingsRules.IsAllowedHosts(AllowedHosts) ? null : Strings.Settings_AllowedHostsInvalid;

    public bool IsValid =>
        DatabasePathProblem is null && AccountsDatabasePathProblem is null && SpondProblem is null && BackupProblem is null && TimeZoneProblem is null &&
        PercentProblem is null && InvitedEventsProblem is null && DefaultEventStarsProblem is null && ExamMinAverageStarsProblem is null && SyncIntervalProblem is null && AllowedHostsProblem is null;
}

/// <summary>One backup Spond login as it is typed: the password is only filled in to set or change it.</summary>
public sealed partial class BackupAccountForm : ObservableObject
{
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _phoneNumber = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _hasPassword;

    public BackupAccountForm()
    {
    }

    public BackupAccountForm(SpondAccountDto saved)
    {
        _email = saved.Email ?? "";
        _phoneNumber = saved.PhoneNumber ?? "";
        _hasPassword = saved.HasPassword;
    }

    public bool IsEmpty => Email.Trim().Length == 0 && PhoneNumber.Trim().Length == 0 && Password.Length == 0;

    /// <summary>Something typed, but not a whole login: it needs the email (or phone number) and the password.</summary>
    public bool IsIncomplete => !IsEmpty && SettingsRules.HasHalfCredentials(Email, PhoneNumber, Password.Length > 0 || HasPassword);

    public SpondAccountInputDto ToInput() => new(Email.Trim(), PhoneNumber.Trim(), Password.Length == 0 ? null : Password);

    public bool Matches(SpondAccountDto saved) =>
        Email.Trim() == (saved.Email ?? "") && PhoneNumber.Trim() == (saved.PhoneNumber ?? "") && Password.Length == 0;
}
