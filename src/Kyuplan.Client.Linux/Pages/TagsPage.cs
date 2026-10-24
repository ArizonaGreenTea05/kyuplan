using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class TagsPage
{
    public static Gtk.Widget Build(UiContext context, TagsViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.Tags, tag =>
        {
            var row = Ui.Row(tag.Name, tag.EventText);
            row.AddSuffix(Ui.Muted(string.Format(Strings.Comments_CountMany, tag.CommentCount)).Css("caption").Tip(Strings.Tags_ColComments));
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Tags_Edit, page.BeginEditCommand, tag, "flat").VisibleWhen(page, nameof(TagsViewModel.CanEdit)));
            row.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.Tags_Delete, page.AskDeleteCommand, tag, "flat").VisibleWhen(page, nameof(TagsViewModel.CanEdit)));
            return row;
        }, [nameof(TagsViewModel.Tags)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Tags_Lede), Ui.Primary(Strings.Tags_New, page.BeginCreateCommand).VisibleWhen(page, nameof(TagsViewModel.CanEdit))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            TagForm.Panel(page.Editor),
            Widgets.ConfirmBar(page, nameof(TagsViewModel.IsConfirmingDelete), nameof(TagsViewModel.ConfirmText), page.ConfirmDeleteCommand, Strings.Tags_ConfirmYes, page.CancelDeleteCommand, Strings.Tags_Cancel),
            Ui.Label(page, nameof(TagsViewModel.EmptyText), "dim-label").VisibleWhen(page, nameof(TagsViewModel.IsEmpty)),
            list);
        return Widgets.PageFrame(page, body, 900);
    }
}
