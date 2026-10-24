using Android.Graphics;
using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>Asks for the address of the server: on the first start, and when the user wants another one.</summary>
public sealed class AddressScreen : Screen
{
    private readonly ApiAddressViewModel _vm;
    private readonly ScrollView _root;
    private readonly TextView _problem;
    private readonly TextView _save;

    /// <param name="canCancel">There is an address to go back to.</param>
    public AddressScreen(Ui ui, ApiAddressViewModel vm, bool canCancel, Action cancelled) : base(ui)
    {
        _vm = vm;
        Observe(vm);

        var address = ui.Input(Strings.ApiAddress_Label, vm.Address, text => vm.Address = text);
        address.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
        address.SetSingleLine(true);
        _problem = ui.Text(Strings.ApiAddress_Invalid, TextStyle.Caption, Tone.Danger);
        _save = ui.Button(Strings.ApiAddress_Save, () =>
        {
            ui.Hide(address);
            if (vm.AcceptCommand.CanExecute(null)) vm.AcceptCommand.Execute(null);
        });

        var form = ui.Column();
        form.SetPadding(ui.Dp(24), ui.Dp(48), ui.Dp(24), ui.Dp(24));
        ui.Add(form, ui.Text(Strings.ApiAddress_Title, TextStyle.Title), bottom: 8);
        ui.Add(form, ui.Text(Strings.ApiAddress_Lede, muted: true), bottom: 16);
        ui.Add(form, address, bottom: 4);
        ui.Add(form, _problem, bottom: 12);
        ui.Add(form, _save, bottom: 8);
        if (canCancel) ui.Add(form, ui.Button(Strings.ApiAddress_Cancel, cancelled, filled: false));

        _root = new ScrollView(ui.Context) { FillViewport = true };
        _root.AddView(form);
    }

    public override View Root => _root;

    protected override void Render()
    {
        _problem.Visibility = _vm.HasProblem ? ViewStates.Visible : ViewStates.Gone;
        _save.Alpha = _vm.AcceptCommand.CanExecute(null) ? 1f : 0.45f;
    }
}
