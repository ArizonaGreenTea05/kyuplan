using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>Backups: download or restore a file, and the mirror databases that are refreshed by the server on a schedule.</summary>
internal static class BackupPage
{
    public static Gtk.Widget Build(UiContext context, BackupViewModel page)
    {
        page.DownloadReady += async (_, file) => await context.SaveAsync(file, Strings.Backup_Download);

        var targets = Ui.BoxedList(page, () => page.Targets, row =>
        {
            var details = $"{row.KindText} · {row.Location} · {row.IntervalText}\n{row.StatusText} · {row.NextText}";
            var r = Ui.Row(row.Name, details);
            if (row.HasFailed) r.AddCssClass("error");
            r.AddSuffix(Ui.Button(Strings.Backup_RunNow, page.RunNowCommand, row).VisibleWhen(page, nameof(BackupViewModel.CanEdit)));
            r.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Backup_Edit, page.BeginEditCommand, row, "flat").VisibleWhen(page, nameof(BackupViewModel.CanEdit)));
            r.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.Backup_Delete, page.AskDeleteCommand, row, "flat").VisibleWhen(page, nameof(BackupViewModel.CanEdit)));
            return r;
        }, [nameof(BackupViewModel.Targets)]);

        var passphrase = Ui.Field(Strings.Backup_PassphraseLabel, Ui.Password(page, nameof(BackupViewModel.Passphrase), Strings.Backup_PassphraseLabel), Strings.Backup_PassphraseHint);
        var download = Widgets.Section(Strings.Backup_Download,
            Ui.Muted(Strings.Backup_Lede),
            passphrase,
            Ui.Label(page, () => page.PassphraseProblem, [nameof(BackupViewModel.PassphraseProblem), nameof(BackupViewModel.Passphrase)], "error")
                .VisibleWhen(page, () => page.PassphraseProblem is not null && page.Passphrase.Length > 0, nameof(BackupViewModel.PassphraseProblem), nameof(BackupViewModel.Passphrase)),
            Ui.Primary(Strings.Backup_Download, page.DownloadCommand).Start()).VisibleWhen(page, nameof(BackupViewModel.CanDownload));

        var restore = Widgets.Section(Strings.Backup_ChooseFile,
            Ui.Muted(Strings.Backup_RestoreHint),
            Ui.Muted(Strings.Backup_FileHint).Css("caption"),
            Ui.Button(Strings.Backup_ChooseFile, () => _ = ChooseFileAsync(context, page)).Start(),
            Ui.HBox(12, Ui.Label(page, nameof(BackupViewModel.ConfirmRestoreText)).Expand(),
                Ui.Button(Strings.Backup_RestoreYes, page.ConfirmRestoreCommand, null, "destructive-action"), Ui.Button(Strings.Backup_Cancel, page.CancelRestoreCommand))
                .Css("banner-problem").VisibleWhen(page, nameof(BackupViewModel.IsConfirmingRestore)),
            Ui.Label(Strings.Setup_Restarting, "accent").VisibleWhen(page, nameof(BackupViewModel.IsRestarting))).VisibleWhen(page, nameof(BackupViewModel.CanRestore));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Backup_Lede)),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            download, restore,
            Targets(page, targets));
        return Widgets.PageFrame(page, body, 1000);
    }

    private static async Task ChooseFileAsync(UiContext context, BackupViewModel page)
    {
        if (await context.PickFileAsync(Strings.Backup_ChooseFile, (Strings.Backup_FileTitle, $"*{BackupRules.Extension};*.zip")) is not { } file || file.GetPath() is not { } path) return;

        page.StageRestore(Path.GetFileName(path), () => File.OpenRead(path));
    }

    private static Gtk.Widget Targets(BackupViewModel page, Gtk.Widget list)
    {
        var kind = Ui.ComboRow(page, Strings.Backup_FormKind, () => page.Kinds, k => k.Label, () => page.Kinds.FirstOrDefault(k => k.Value == page.FormKind),
            k => page.FormKind = k.Value, nameof(BackupViewModel.FormKind));
        var interval = Ui.ComboRow(page, Strings.Backup_FormInterval, () => page.Intervals, i => i.Label, () => page.Intervals.FirstOrDefault(i => i.Value == page.FormIntervalMinutes),
            i => page.FormIntervalMinutes = i.Value, nameof(BackupViewModel.FormIntervalMinutes));

        var save = Ui.Primary(Strings.Backup_Save, page.SaveCommand);
        var form = Ui.VBox(10,
            Ui.Label(page, nameof(BackupViewModel.FormTitle), "heading").Start(),
            Ui.Group(null, Ui.EntryRow(page, nameof(BackupViewModel.FormName), Strings.Backup_FormName), kind,
                Ui.EntryRow(page, nameof(BackupViewModel.FormLocation), Strings.Backup_LocationSqlite), interval,
                Ui.SwitchRow(page, nameof(BackupViewModel.FormEnabled), Strings.Backup_FormEnabled)),
            Ui.Label(page, nameof(BackupViewModel.LocationHint), "dim-label", "caption"),
            Ui.HBox(8, save, Ui.Button(Strings.Backup_Cancel, page.CancelFormCommand)))
            .Css("card").Padded(14).VisibleWhen(page, nameof(BackupViewModel.IsFormOpen));

        return Widgets.Section(Strings.Backup_TargetsTitle,
            Ui.Muted(Strings.Backup_TargetsHint),
            Ui.Button(Strings.Backup_New, page.BeginCreateCommand).Start().VisibleWhen(page, nameof(BackupViewModel.CanEdit)),
            form,
            Widgets.ConfirmBar(page, nameof(BackupViewModel.IsConfirmingDelete), nameof(BackupViewModel.ConfirmDeleteText), page.ConfirmDeleteCommand, Strings.Backup_ConfirmYes, page.CancelDeleteCommand, Strings.Backup_Cancel),
            Ui.Label(page, nameof(BackupViewModel.EmptyText), "dim-label").VisibleWhen(page, nameof(BackupViewModel.IsEmpty)),
            list);
    }
}
