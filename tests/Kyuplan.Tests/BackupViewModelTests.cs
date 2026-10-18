using System.Globalization;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The backup page: backup databases, the download of a backup file and the replacement of all data with one.</summary>
public sealed class BackupViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly BackupViewModel _vm;

    public BackupViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);
        _shell = new ShellViewModel(_api, _language);
        _vm = new BackupViewModel(_shell, _api, new ApiRestartWatcher(_api) { Delay = (_, _) => Task.CompletedTask });
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<BackupViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private async Task AddTargetAsync(BackupViewModel vm, string name = "Nightly", string folder = @"D:\backups", int minutes = 1440)
    {
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = name;
        vm.FormLocation = folder;
        vm.FormIntervalMinutes = minutes;
        await vm.SaveCommand.ExecuteAsync(null);
    }

    // --- Who gets to see what ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_for_somebody_who_may_not_see_backups_does_not_even_ask_for_them()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false);

        var vm = await ShownAsync();

        Assert.False(vm.CanView);
        Assert.Empty(vm.Targets);
        Assert.Empty(_api.BackupCalls);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Somebody_who_may_only_see_the_page_cannot_change_download_or_restore()
    {
        _api.Account = WithPermissions(2, "mia", Permission.BackupsView);
        _api.BackupTargets.Add(new BackupTargetDto(1, "Nightly", BackupTargetKind.Sqlite, "x", 60, true, null, null, null, null));

        var vm = await ShownAsync();

        Assert.Single(vm.Targets);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanDownload);
        Assert.False(vm.CanRestore);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(vm.Targets[0]));
        Assert.False(vm.AskDeleteCommand.CanExecute(vm.Targets[0]));
        Assert.False(vm.RunNowCommand.CanExecute(vm.Targets[0]));
        Assert.False(vm.DownloadCommand.CanExecute(null));

        await vm.DownloadCommand.ExecuteAsync(null); // Execute does not ask CanExecute
        vm.StageRestore("a.kyubackup", () => new MemoryStream([1]));
        Assert.False(vm.IsConfirmingRestore);
        Assert.Empty(_api.BackupCalls);
    }

    // --- Backup databases --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_backup_database_is_created_and_listed()
    {
        var vm = await ShownAsync();
        Assert.True(vm.IsEmpty);

        vm.BeginCreateCommand.Execute(null);
        Assert.False(vm.SaveCommand.CanExecute(null)); // nothing typed yet
        await AddTargetAsync(vm, "Nightly", @"D:\backups", 360);

        var row = Assert.Single(vm.Targets);
        Assert.Equal("Nightly", row.Name);
        Assert.Equal("every 6 h", row.IntervalText);
        Assert.Equal("Has not been updated yet", row.StatusText);
        Assert.False(vm.IsFormOpen);
        Assert.Equal("Backup database “Nightly” added.", vm.NoticeText);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task A_form_that_the_rules_refuse_cannot_be_saved()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Nightly";
        vm.FormLocation = @"D:\backups";
        Assert.True(vm.CanSave);

        vm.FormName = "  ";
        Assert.False(vm.CanSave);
        vm.FormName = "Nightly";
        vm.FormLocation = "";
        Assert.False(vm.CanSave);
        vm.FormLocation = @"D:\backups";
        vm.FormIntervalMinutes = 1;
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task A_backup_database_is_changed_in_the_form_it_was_created_in()
    {
        var vm = await ShownAsync();
        await AddTargetAsync(vm, "Nightly", @"D:\backups", 90); // an interval that is no preset

        vm.BeginEditCommand.Execute(vm.Targets[0]);
        Assert.Equal("Nightly", vm.FormName);
        Assert.Contains(vm.Intervals, i => i.Value == 90 && i.Label == "every 90 min");
        vm.FormIntervalMinutes = 60;
        vm.FormEnabled = false;
        await vm.SaveCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Targets);
        Assert.Equal(60, row.Target.IntervalMinutes);
        Assert.False(row.Enabled);
        Assert.Equal("Switched off", row.NextText, ignoreCase: true);
        Assert.Contains("update:1", _api.BackupCalls);
    }

    [Fact]
    public async Task A_refusal_of_the_api_is_shown_and_the_form_stays_open()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Nightly";
        vm.FormLocation = @"D:\backups";
        _api.FailWith = new PlannerApiException("There is a backup database called 'Nightly' already.", System.Net.HttpStatusCode.BadRequest);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsFormOpen);
        Assert.Equal("There is a backup database called 'Nightly' already.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Running_a_backup_database_reports_how_it_went()
    {
        var vm = await ShownAsync();
        await AddTargetAsync(vm);

        await vm.RunNowCommand.ExecuteAsync(vm.Targets[0]);
        Assert.Equal("“Nightly” is up to date.", vm.NoticeText);
        Assert.StartsWith("Last update", vm.Targets[0].StatusText);

        _api.BackupRunFails = "connection refused";
        await vm.RunNowCommand.ExecuteAsync(vm.Targets[0]);
        Assert.Equal("Updating “Nightly” failed: connection refused", vm.NoticeText);
        Assert.True(vm.Targets[0].HasFailed);
        Assert.Contains("connection refused", vm.Targets[0].StatusText);
        Assert.False(vm.HasError); // a failed run is an outcome, not an error of the page
    }

    [Fact]
    public async Task Nothing_is_deleted_before_the_question_is_answered()
    {
        var vm = await ShownAsync();
        await AddTargetAsync(vm);

        vm.AskDeleteCommand.Execute(vm.Targets[0]);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Contains("Nightly", vm.ConfirmDeleteText);
        vm.CancelDeleteCommand.Execute(null);
        Assert.Single(vm.Targets);
        Assert.DoesNotContain("delete:1", _api.BackupCalls);

        vm.AskDeleteCommand.Execute(vm.Targets[0]);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Empty(vm.Targets);
        Assert.True(vm.IsEmpty);
        Assert.Equal("Backup database “Nightly” deleted.", vm.NoticeText);
    }

    [Fact]
    public async Task The_texts_follow_the_language()
    {
        var vm = await ShownAsync();
        await AddTargetAsync(vm, minutes: 1440);
        Assert.Equal("every 1 d", vm.Targets[0].IntervalText);

        _language.Select("de");

        Assert.Equal("alle 1 Tage", vm.Targets[0].IntervalText);
        Assert.Equal("Datensicherung", vm.Title);
    }

    // --- The backup file ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_is_handed_to_the_client_to_save()
    {
        var vm = await ShownAsync();
        BackupFile? received = null;
        vm.DownloadReady += (_, file) => received = file;

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal("kyuplan-backup-test.kyubackup", received?.FileName);
        Assert.Equal([1, 2, 3], received?.Content);
        Assert.Equal("The backup file was downloaded.", vm.NoticeText);
    }

    [Fact]
    public async Task A_download_that_fails_is_not_saved()
    {
        var vm = await ShownAsync();
        var received = false;
        vm.DownloadReady += (_, _) => received = true;
        _api.FailWith = new PlannerApiException("no", System.Net.HttpStatusCode.Forbidden);

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.False(received);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task All_data_is_replaced_only_after_the_user_confirmed_and_the_api_came_back()
    {
        var vm = await ShownAsync();

        vm.StageRestore("old.kyubackup", () => new MemoryStream([9, 9, 9, 9]));
        Assert.True(vm.IsConfirmingRestore);
        Assert.Contains("old.kyubackup", vm.ConfirmRestoreText);
        Assert.Empty(_api.BackupCalls); // nothing was sent yet

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.Equal(["restore:4"], _api.BackupCalls);
        Assert.False(vm.IsConfirmingRestore);
        Assert.False(vm.IsRestarting);
        Assert.Contains("restored data", vm.NoticeText);
    }

    [Fact]
    public async Task A_cancelled_restore_sends_nothing()
    {
        var vm = await ShownAsync();
        vm.StageRestore("old.kyubackup", () => new MemoryStream([1]));

        vm.CancelRestoreCommand.Execute(null);
        await vm.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmingRestore);
        Assert.Empty(_api.BackupCalls);
    }

    [Fact]
    public async Task A_file_the_api_refuses_is_reported_and_the_page_does_not_wait_for_a_restart()
    {
        var vm = await ShownAsync();
        vm.StageRestore("junk.kyubackup", () => new MemoryStream()); // the fake refuses an empty file

        await vm.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.Equal("This is not a backup file.", vm.ErrorMessage);
        Assert.False(vm.IsRestarting);
        Assert.False(vm.HasNotice);
    }
}
