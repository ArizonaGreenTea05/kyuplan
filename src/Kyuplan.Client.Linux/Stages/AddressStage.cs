using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Stages;

/// <summary>Asks for the address of the API: on the first start, and when the user wants to sign in to another server.</summary>
internal static class AddressStage
{
    public static Gtk.Widget Build(UiContext context, ApiAddressViewModel model, Action? onCancel)
    {
        var entry = Ui.Entry(model, nameof(ApiAddressViewModel.Address), "https://planner.example.org", Strings.ApiAddress_Label);
        entry.InputPurpose = Gtk.InputPurpose.Url;
        entry.OnActivate += (_, _) => model.AcceptCommand.Execute(null);

        var problem = Ui.Label(Strings.ApiAddress_Invalid, "error").VisibleWhen(model, nameof(ApiAddressViewModel.HasProblem));
        var buttons = Ui.HBox(10).End();
        if (onCancel is not null) buttons.Append(Ui.Button(Strings.ApiAddress_Cancel, onCancel));
        else buttons.Append(Ui.Button(Strings.Login_Exit, () => context.Window.Close()));
        buttons.Append(Ui.Primary(Strings.ApiAddress_Save, model.AcceptCommand));

        var content = Ui.VBox(14,
            Ui.Label(Strings.ApiAddress_Title, "title-1"),
            Ui.Muted(Strings.ApiAddress_Lede),
            Ui.Field(Strings.ApiAddress_Label, entry),
            problem,
            buttons);
        entry.GrabFocus();
        return Frame.Centered(content);
    }
}
