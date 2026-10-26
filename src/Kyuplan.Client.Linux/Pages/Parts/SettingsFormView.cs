using System.ComponentModel;
using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages.Parts;

/// <summary>The settings of the API as cards (<see cref="SettingsForm"/>): the same on the setup screen and on the settings page. Each field says what is wrong with it as soon as it is.</summary>
internal static class SettingsFormView
{
    public static Gtk.Widget Build(SettingsForm form)
    {
        Gtk.Widget Field(string caption, Gtk.Widget widget, Func<string?> problem, string problemProperty, string? hint = null)
        {
            var box = Ui.VBox(2, Ui.Field(caption, widget, hint));
            box.Append(Ui.Label(form, problem, [problemProperty], "error", "caption").VisibleWhen(form, () => !string.IsNullOrEmpty(problem()), problemProperty));
            return box;
        }

        Gtk.Widget Text(string caption, string property, Func<string?> problem, string problemProperty, string? hint = null) =>
            Field(caption, Ui.Entry(form, property, name: caption), problem, problemProperty, hint);

        Gtk.Widget Number(string caption, string property, double min, double max, double step, uint digits, Func<string?> problem, string problemProperty) =>
            Field(caption, Ui.Spin(form, property, min, max, step, digits, caption), problem, problemProperty);

        var databases = Widgets.Section(Strings.Settings_DatabasesTitle,
            Text(Strings.Settings_DatabasePath, nameof(SettingsForm.DatabasePath), () => form.DatabasePathProblem, nameof(SettingsForm.DatabasePathProblem)),
            Text(Strings.Settings_AccountsDatabasePath, nameof(SettingsForm.AccountsDatabasePath), () => form.AccountsDatabasePathProblem, nameof(SettingsForm.AccountsDatabasePathProblem)));

        var dataSource = Ui.Choice(form, () => SettingsForm.DataSources, SettingsForm.DataSourceText, () => form.DataSource, kind => form.DataSource = kind,
            [nameof(SettingsForm.DataSource)], Strings.Settings_DataSource);

        var script = Ui.TextArea(form, nameof(SettingsForm.CustomScript), out var scriptView, 320, Strings.Settings_CustomScript);
        scriptView.AddCssClass("monospace");
        scriptView.EnabledWhen(form, () => !form.CustomScriptHidden, nameof(SettingsForm.CustomScriptHidden));
        var secrets = Ui.VBox(8,
            Ui.Label(Strings.Settings_ScriptSecrets, "caption-heading").Start(),
            Ui.Muted(Strings.Settings_ScriptSecretsHint).Css("caption"),
            Ui.Repeat(form, () => form.ScriptSecrets, secret => ScriptSecret(form, secret), [nameof(SettingsForm.ScriptSecrets)], 8),
            Ui.Label(form, () => form.ScriptSecretsProblem, [nameof(SettingsForm.ScriptSecretsProblem)], "error", "caption").VisibleWhen(form, () => form.ScriptSecretsProblem is not null, nameof(SettingsForm.ScriptSecretsProblem)),
            Ui.Button(Strings.Settings_ScriptSecretAdd, form.AddScriptSecretCommand).Start());

        var custom = Ui.VBox(8,
            Field(Strings.Settings_CustomScript, script, () => form.CustomScriptProblem, nameof(SettingsForm.CustomScriptProblem)),
            Ui.Muted(Strings.Settings_ScriptHidden).Css("caption").VisibleWhen(form, () => form.CustomScriptHidden, nameof(SettingsForm.CustomScriptHidden)),
            Ui.Label(form, () => form.CustomScriptWarning, [nameof(SettingsForm.CustomScriptWarning)], "error", "caption").VisibleWhen(form, () => form.CustomScriptWarning is not null, nameof(SettingsForm.CustomScriptWarning)),
            Ui.Button(Strings.Settings_ScriptTemplate, form.InsertScriptTemplateCommand).Start(),
            secrets)
            .VisibleWhen(form, nameof(SettingsForm.IsCustomSource));

        var backup = Ui.VBox(8,
            Ui.Label(Strings.Settings_BackupAccounts, "caption-heading").Start(),
            Ui.Muted(Strings.Settings_BackupAccountsHint).Css("caption"),
            Ui.Repeat(form, () => form.BackupAccounts, account => BackupAccount(form, account), [nameof(SettingsForm.BackupAccounts)], 8),
            Ui.Label(form, () => form.BackupProblem, [nameof(SettingsForm.BackupProblem)], "error", "caption").VisibleWhen(form, () => form.BackupProblem is not null, nameof(SettingsForm.BackupProblem)),
            Ui.Button(Strings.Settings_BackupAdd, form.AddBackupAccountCommand).Start());

        var spond = Ui.VBox(10,
            Ui.Flow(
                Ui.Field(Strings.Settings_SpondEmail, Ui.Entry(form, nameof(SettingsForm.SpondEmail), name: Strings.Settings_SpondEmail)),
                Ui.Field(Strings.Settings_SpondPhone, Ui.Entry(form, nameof(SettingsForm.SpondPhoneNumber), name: Strings.Settings_SpondPhone))),
            Field(Strings.Settings_SpondPassword, Ui.Password(form, nameof(SettingsForm.SpondPassword), Strings.Settings_SpondPassword), () => form.SpondProblem, nameof(SettingsForm.SpondProblem)),
            backup,
            Ui.Field(Strings.Settings_UnregisteredSubGroup,
                Ui.Choice(form, () => [string.Empty, .. form.SubGroupChoices], n => n.Length == 0 ? Strings.Settings_NoSubGroup : n, () => form.UnregisteredSubGroup, n => form.UnregisteredSubGroup = n,
                    [nameof(SettingsForm.UnregisteredSubGroup), nameof(SettingsForm.SubGroupChoices), nameof(SettingsForm.AvailableSubGroups)], Strings.Settings_UnregisteredSubGroup)))
            .VisibleWhen(form, nameof(SettingsForm.IsSpondSource));

        var source = Widgets.Section(Strings.Settings_DataSource,
            Ui.Field(Strings.Settings_DataSource, dataSource, null),
            Ui.Label(form, nameof(SettingsForm.DataSourceHint), "dim-label", "caption"),
            custom, spond);

        var statistics = Widgets.Section(Strings.Settings_StatisticsTitle,
            Ui.Flow(
                Text(Strings.Settings_TimeZone, nameof(SettingsForm.TimeZone), () => form.TimeZoneProblem, nameof(SettingsForm.TimeZoneProblem)),
                Ui.Field(Strings.Settings_ExamKeyword, Ui.Entry(form, nameof(SettingsForm.ExamKeyword), name: Strings.Settings_ExamKeyword))),
            Ui.Flow(
                Number(Strings.Settings_MinAttendance, nameof(SettingsForm.DefaultMinAttendancePercent), 0, 100, 1, 0, () => form.PercentProblem, nameof(SettingsForm.PercentProblem)),
                Number(Strings.Settings_MinInvited, nameof(SettingsForm.DefaultMinInvitedEvents), 0, 1000, 1, 0, () => form.InvitedEventsProblem, nameof(SettingsForm.InvitedEventsProblem))),
            Ui.Flow(
                Number(Strings.Settings_DefaultEventStars, nameof(SettingsForm.DefaultEventStars), 0, 5, 0.1, 1, () => form.DefaultEventStarsProblem, nameof(SettingsForm.DefaultEventStarsProblem)),
                Number(Strings.Settings_ExamMinStars, nameof(SettingsForm.ExamMinAverageStars), 0, 5, 0.1, 1, () => form.ExamMinAverageStarsProblem, nameof(SettingsForm.ExamMinAverageStarsProblem))));

        var sync = Widgets.Section(Strings.Settings_SyncTitle,
            Ui.HBox(10, Ui.Switch(form, nameof(SettingsForm.SyncEnabled)),
                Ui.VBox(0, Ui.Label(Strings.Settings_SyncEnabled), Ui.Muted(Strings.Settings_SyncEnabledHint).Css("caption")).Expand()),
            Number(Strings.Settings_SyncInterval, nameof(SettingsForm.SyncIntervalMinutes), 0, SettingsRules.MaxSyncIntervalMinutes, 1, 0, () => form.SyncIntervalProblem, nameof(SettingsForm.SyncIntervalProblem))
                .VisibleWhen(form, nameof(SettingsForm.SyncEnabled)))
            .VisibleWhen(form, () => !form.IsManualSource, nameof(SettingsForm.DataSource));

        var security = Widgets.Section(Strings.Settings_SecurityTitle,
            Text(Strings.Settings_AllowedHosts, nameof(SettingsForm.AllowedHosts), () => form.AllowedHostsProblem, nameof(SettingsForm.AllowedHostsProblem)),
            Ui.HBox(10, Ui.Switch(form, nameof(SettingsForm.MetricsEnabled)),
                Ui.VBox(0, Ui.Label(Strings.Settings_MetricsEnabled), Ui.Muted(Strings.Settings_MetricsEnabledHint).Css("caption")).Expand()),
            Field(Strings.Settings_MetricsToken, Ui.Password(form, nameof(SettingsForm.MetricsToken), Strings.Settings_MetricsToken), () => form.MetricsTokenProblem, nameof(SettingsForm.MetricsTokenProblem))
                .VisibleWhen(form, nameof(SettingsForm.MetricsEnabled)));

        return Ui.VBox(14, databases, source, statistics, sync, security);
    }

    private static Gtk.Widget ScriptSecret(SettingsForm form, ScriptSecretForm secret) =>
        Ui.VBox(6,
            Ui.Flow(
                Ui.Field(Strings.Settings_ScriptSecretName, Ui.Entry(secret, nameof(ScriptSecretForm.Name), name: Strings.Settings_ScriptSecretName)),
                Ui.Field(Strings.Settings_ScriptSecretValue, Ui.Password(secret, nameof(ScriptSecretForm.Value), Strings.Settings_ScriptSecretValue))),
            Ui.Label(secret, () => Strings.Settings_ScriptSecretStored, [nameof(ScriptSecretForm.HasValue)], "dim-label", "caption")
                .VisibleWhen(secret, () => secret.HasValue, nameof(ScriptSecretForm.HasValue)),
            Ui.Button(Strings.Settings_ScriptSecretRemove, form.RemoveScriptSecretCommand, secret).Start())
            .Css("card").Padded(10);

    private static Gtk.Widget BackupAccount(SettingsForm form, BackupAccountForm account) =>
        Ui.VBox(6,
            Ui.Flow(
                Ui.Field(Strings.Settings_SpondEmail, Ui.Entry(account, nameof(BackupAccountForm.Email), name: Strings.Settings_SpondEmail)),
                Ui.Field(Strings.Settings_SpondPhone, Ui.Entry(account, nameof(BackupAccountForm.PhoneNumber), name: Strings.Settings_SpondPhone)),
                Ui.Field(Strings.Settings_SpondPassword, Ui.Password(account, nameof(BackupAccountForm.Password), Strings.Settings_SpondPassword))),
            Ui.Button(Strings.Settings_BackupRemove, form.RemoveBackupAccountCommand, account).Start())
            .Css("card").Padded(10);
}
