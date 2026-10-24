using System.Windows.Input;
using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class BeltsPage
{
    public static Gtk.Widget Build(UiContext context, BeltsViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.Belts, belt =>
        {
            var row = Ui.Row(belt.Name, $"{belt.PrerequisitesText} · {Strings.Belts_ColMembers}: {belt.MemberCount}", null, ColorClass.Belt(belt.PrimaryColor, belt.SecondaryColor));
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Belts_Edit, page.BeginEditCommand, belt, "flat").VisibleWhen(page, nameof(BeltsViewModel.CanEdit)));
            var delete = Ui.IconButton("user-trash-symbolic", belt.DeleteBlockedText ?? Strings.Belts_Delete, page.AskDeleteCommand, belt, "flat");
            delete.Sensitive = belt.CanDelete;
            delete.VisibleWhen(page, nameof(BeltsViewModel.CanEdit));
            row.AddSuffix(delete);
            return row;
        }, [nameof(BeltsViewModel.Belts)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Belts_Lede), Ui.Primary(Strings.Belts_New, page.BeginCreateCommand).VisibleWhen(page, nameof(BeltsViewModel.CanEdit))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Form(page),
            Widgets.ConfirmBar(page, nameof(BeltsViewModel.IsConfirmingDelete), nameof(BeltsViewModel.ConfirmText), page.ConfirmDeleteCommand, Strings.Belts_ConfirmYes, page.CancelDeleteCommand, Strings.Belts_Cancel),
            Ui.Label(page, nameof(BeltsViewModel.EmptyText), "dim-label").VisibleWhen(page, nameof(BeltsViewModel.IsEmpty)),
            list);
        return Widgets.PageFrame(page, body, 900);
    }

    private static Gtk.Widget Form(BeltsViewModel page)
    {
        var save = Ui.Primary("", page.SaveCommand);
        Bind.Watch(save, page, () => save.SetLabel(page.IsCreating ? Strings.Belts_Create : Strings.Belts_Save), nameof(BeltsViewModel.IsCreating));

        var form = Ui.VBox(12,
            Ui.Label(page, nameof(BeltsViewModel.FormTitle), "heading").Start(),
            Ui.Field(Strings.Belts_FormName, Ui.Entry(page, nameof(BeltsViewModel.FormName), name: Strings.Belts_FormName)),
            Ui.Flow(
                ColorField(page, Strings.Belts_FormColor, nameof(BeltsViewModel.FormPrimaryColor), nameof(BeltsViewModel.PrimaryPreview),
                    () => page.PrimaryColorProblem, nameof(BeltsViewModel.PrimaryColorProblem), page.PickPrimaryColorCommand, null),
                ColorField(page, Strings.Belts_FormStripe, nameof(BeltsViewModel.FormSecondaryColor), nameof(BeltsViewModel.SecondaryPreview),
                    () => page.SecondaryColorProblem, nameof(BeltsViewModel.SecondaryColorProblem), page.PickSecondaryColorCommand, page.ClearSecondaryColorCommand)),
            Ui.VBox(6,
                Ui.Label(Strings.Belts_FormPrerequisites, "caption-heading").Start(),
                Ui.Muted(Strings.Belts_FormPrerequisitesHint).Css("caption"),
                Ui.Muted(Strings.Belts_FormNoOtherBelts).VisibleWhen(page, () => page.PrerequisiteOptions.Count == 0, nameof(BeltsViewModel.PrerequisiteOptions)),
                Ui.Repeat(page, () => page.PrerequisiteOptions, Prerequisite, [nameof(BeltsViewModel.PrerequisiteOptions)], 6)),
            Ui.HBox(8, save, Ui.Button(Strings.Belts_Cancel, page.CancelFormCommand)));
        return form.Css("card").Padded(14).VisibleWhen(page, nameof(BeltsViewModel.IsFormOpen));
    }

    /// <summary>A colour: its preview, the typed value, the problem with it, and the presets to pick from.</summary>
    private static Gtk.Widget ColorField(BeltsViewModel page, string caption, string property, string preview, Func<string?> problem, string problemProperty,
        ICommand pick, ICommand? clear)
    {
        var swatch = Ui.VBox(0);
        Bind.Watch(swatch, page, () =>
        {
            while (swatch.GetFirstChild() is { } child) swatch.Remove(child);
            swatch.Append(ColorClass.Swatch(Bind.GetRaw(page, preview) as string, 36, 28));
        }, preview);

        var entry = Ui.Entry(page, property, name: caption);
        var line = Ui.HBox(8, swatch, entry);
        if (clear is not null) line.Append(Ui.Button(Strings.Belts_NoStripe, clear));

        var presets = Ui.Flow();
        foreach (var color in BeltsViewModel.ColorPresets)
        {
            var button = Gtk.Button.New();
            button.SetChild(ColorClass.Swatch(color, 22, 22));
            button.AddCssClass("flat");
            button.Tip(color);
            button.OnClicked += (_, _) => pick.Execute(color);
            presets.Append(button);
        }

        return Ui.VBox(6,
            Ui.Label(caption, "caption-heading").Start(), line,
            Ui.Label(page, problem, [problemProperty], "error", "caption").VisibleWhen(page, () => problem() is not null, problemProperty),
            Ui.Muted(Strings.Belts_ColorHint).Css("caption"), presets).Expand();
    }

    /// <summary>A belt that can be required: ticked, with the age and waiting time that apply to members coming from it.</summary>
    private static Gtk.Widget Prerequisite(BeltPrerequisiteOption option)
    {
        string[] problems = [nameof(BeltPrerequisiteOption.MinAgeProblem), nameof(BeltPrerequisiteOption.MinWaitProblem), nameof(BeltPrerequisiteOption.IsSelected)];
        var problem = Ui.Label(option, () => option.MinAgeProblem ?? option.MinWaitProblem, problems, "error", "caption")
            .VisibleWhen(option, () => option.IsSelected && (option.MinAgeProblem ?? option.MinWaitProblem) is not null, problems);
        var numbers = Ui.HBox(8,
            Ui.Caption(Strings.Belts_ColMinAge, "dim-label"), Ui.Spin(option, nameof(BeltPrerequisiteOption.MinAgeYears), 0, 120, 1, 0, Strings.Belts_ColMinAge),
            Ui.Caption(Strings.Belts_ColMinWait, "dim-label"), Ui.Spin(option, nameof(BeltPrerequisiteOption.MinWaitMonths), 0, 600, 1, 0, Strings.Belts_ColMinWait))
            .EnabledWhen(option, nameof(BeltPrerequisiteOption.IsSelected));
        return Ui.VBox(2, Ui.HBox(10, Ui.Check(option, nameof(BeltPrerequisiteOption.IsSelected), option.Name).Expand(), ColorClass.Swatch(option.Color, 28, 14), numbers), problem);
    }
}
