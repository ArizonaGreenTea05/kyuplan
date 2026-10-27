using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>The members of the selected group: a list with a search box, and one member's details.</summary>
public sealed class MembersScreen : Screen
{
    private readonly MembersViewModel _vm;
    private readonly FrameLayout _root;
    private readonly LinearLayout _list;
    private readonly TextView _count;
    private readonly TextView _message;
    private readonly ProgressBar _busy;
    private readonly ListView _listView;
    private readonly MemberAdapter _adapter;
    private readonly ScrollView _detailScroll;
    private readonly LinearLayout _detail;
    private MemberRow? _open;

    public MembersScreen(Ui ui, MembersViewModel vm) : base(ui)
    {
        _vm = vm;
        Observe(vm);
        Observe(vm.Shell, nameof(ShellViewModel.SelectedGroupId), nameof(ShellViewModel.Groups));
        Observe(vm.Shell.Language);

        _adapter = new MemberAdapter(ui);
        _listView = new ListView(ui.Context) { Adapter = _adapter, Divider = null, DividerHeight = 0 };
        _listView.SetPadding(ui.Dp(16), 0, ui.Dp(16), ui.Dp(16));
        _listView.SetClipToPadding(false);
        _listView.ItemClick += (_, e) =>
        {
            ui.Hide(_listView);
            _open = _adapter.Items[e.Position];
            Invalidate();
        };

        var search = ui.Input(Strings.Nav_Members, vm.SearchText, text => vm.SearchText = text);
        search.InputType = Android.Text.InputTypes.ClassText;
        search.SetCompoundDrawablesWithIntrinsicBounds(Resource.Drawable.ic_search, 0, 0, 0);
        search.CompoundDrawablePadding = ui.Dp(8);
        search.SetSingleLine(true);
        _count = ui.Text("", TextStyle.Caption);
        _message = ui.Text("", muted: true);
        _busy = ui.Busy();

        _list = ui.Column();
        _list.AddView(_busy);
        _list.AddView(search, ui.Params(left: 16, right: 16, top: 8));
        _list.AddView(_count, ui.Params(left: 16, right: 16, top: 8, bottom: 8));
        _list.AddView(_message, ui.Params(left: 16, right: 16));
        _list.AddView(_listView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        _detail = ui.Column();
        _detail.SetPadding(ui.Dp(16), ui.Dp(8), ui.Dp(16), ui.Dp(24));
        _detailScroll = new ScrollView(ui.Context) { FillViewport = true, Visibility = ViewStates.Gone };
        _detailScroll.AddView(_detail);

        _root = new FrameLayout(ui.Context);
        _root.AddView(_list);
        _root.AddView(_detailScroll);
    }

    public override View Root => _root;

    public override bool HandleBack()
    {
        // Back from the form is back to the member, not to the list.
        if (_vm.IsFormOpen)
        {
            Run(_vm.CancelFormCommand);
            return true;
        }

        if (_open is null) return false;

        _open = null;
        Invalidate();
        return true;
    }

    protected override void Render()
    {
        _busy.Visibility = _vm.IsBusy ? ViewStates.Visible : ViewStates.Invisible;
        _adapter.Items = _vm.VisibleMembers;
        _count.Text = _vm.HasLoaded ? _vm.CountText : "";
        var status = (_vm.HasError ? _vm.ErrorMessage
            : string.IsNullOrEmpty(_vm.Shell.SelectedGroupId) ? Strings.Calendar_NoGroup
            : _vm.IsNobodyHere ? Strings.MemberPage_NoneHidden
            : _vm.NoneMatch ? Strings.MemberPage_NoneMatch
            : "") ?? "";
        var saved = status.Length == 0 && _vm.HasNotice; // "Anna saved.": where a save leaves the user
        var text = saved ? _vm.NoticeText : status;
        _message.Text = text;
        _message.Visibility = text.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        _message.SetTextColor(_vm.HasError ? Ui.P.Danger : saved ? Ui.P.Success : Ui.P.Muted);

        if (_open is not null && !_vm.Members.Contains(_open)) _open = _vm.Members.FirstOrDefault(m => m.Id == _open.Id);
        _list.Visibility = _open is null ? ViewStates.Visible : ViewStates.Gone;
        _detailScroll.Visibility = _open is null ? ViewStates.Gone : ViewStates.Visible;
        if (_open is not null) ShowDetail(_open);
    }

    private void ShowDetail(MemberRow member)
    {
        _detail.RemoveAllViews();
        if (_vm.IsFormOpen)
        {
            ShowForm();
            return;
        }

        Ui.Add(_detail, Ui.Row(Ui.IconButton(Resource.Drawable.ic_back, Strings.MemberPage_BackToMembers, () => HandleBack()), Ui.Text(Strings.MemberPage_BackToMembers, TextStyle.Caption)));

        var belt = Ui.Row();
        if (member.HasBelt) belt.AddView(Ui.BeltSwatch(member.Belt.Color, member.Belt.SecondaryColor, right: 8));
        belt.AddView(Ui.Text(member.Belt.Label, TextStyle.Body), Ui.Params(wrap: true));
        Ui.Add(_detail, Ui.Card(Ui.Text(member.Name, TextStyle.Title), Ui.Space(4), belt), bottom: 12);

        Ui.Add(_detail, Ui.Card(
            Field(Strings.MemberPage_ColBeltSince, member.BeltSinceText),
            Field(Strings.MemberPage_ColStarted, member.StartedText),
            Field(Strings.MemberPage_ColBirth, member.BirthText),
            Field(Strings.MemberPage_ColRegistered, member.RegisteredText)));
        if (_vm.BeginEditCommand.CanExecute(member)) Ui.Add(_detail, Ui.Button(Strings.MemberPage_Edit, () => Run(_vm.BeginEditCommand, member)), top: 12);
        if (_vm.MayViewComments(member)) Ui.Add(_detail, Ui.Text(member.CommentsText, TextStyle.Caption), top: 12);
    }

    /// <summary>The belt and the days of the member being edited (<see cref="MembersViewModel.Editing"/>), as the other clients have them.</summary>
    private void ShowForm()
    {
        var form = Ui.Card(Ui.Text(_vm.FormTitle, TextStyle.Heading));
        if (_vm.IsBusy) form.AddView(Ui.Busy()); // the bar of the list is out of sight while a member is open
        if (_vm.HasError) form.AddView(Ui.Notice(_vm.ErrorMessage!, Tone.Danger), Ui.Params(top: 12));

        var options = _vm.BeltOptions;
        var belt = _vm.FormBelt ?? options[0]; // the "no belt" entry comes first
        form.AddView(
            Ui.Picker(Strings.Belts_ColBelt, belt.Label,
                () => Ui.PickOne(Strings.Belts_ColBelt, options, options.ToList().IndexOf(belt), BeltChoice, option => _vm.FormBelt = option),
                belt.IsNone ? null : Ui.BeltSwatch(belt.Color, belt.SecondaryColor, right: 8)),
            Ui.Params(top: 12));

        form.AddView(DateField(Strings.MemberPage_ColStarted, _vm.FormStartedAt, day => _vm.FormStartedAt = day,
            Strings.MemberPage_FormStartedHint, _vm.StartedAtProblem, _vm.ClearStartedAtCommand, Strings.MemberPage_ClearStarted), Ui.Params(top: 16));
        form.AddView(DateField(Strings.MemberPage_ColBirth, _vm.FormBirthDate, day => _vm.FormBirthDate = day,
            Strings.MemberPage_FormBirthHint, _vm.BirthDateProblem, _vm.ClearBirthDateCommand, Strings.MemberPage_ClearBirth), Ui.Params(top: 16));

        // The day the belt was received is for a member who has one.
        if (_vm.CanSetBeltSince)
            form.AddView(DateField(Strings.MemberPage_ColBeltSince, _vm.FormBeltSince, day => _vm.FormBeltSince = day,
                Strings.MemberPage_FormBeltSinceHint, _vm.BeltSinceProblem, _vm.ClearBeltSinceCommand, Strings.MemberPage_ClearStarted), Ui.Params(top: 16));
        else
            form.AddView(Ui.Column(Ui.Text(Strings.MemberPage_ColBeltSince, TextStyle.Caption), Ui.Text(Strings.MemberPage_BeltSinceNeedsBelt, muted: true)), Ui.Params(top: 16));

        Ui.Add(_detail, form, bottom: 12);

        var buttons = Ui.Row(
            Ui.Button(Strings.MemberPage_Save, () => _ = SaveAsync(), enabled: _vm.SaveCommand.CanExecute(null)),
            Ui.Button(Strings.MemberPage_Cancel, () => Run(_vm.CancelFormCommand), filled: false));
        buttons.GetChildAt(0)!.LayoutParameters = Ui.Params(weight: 1, right: 4);
        buttons.GetChildAt(1)!.LayoutParameters = Ui.Params(weight: 1, left: 4);
        _detail.AddView(buttons);
    }

    /// <summary>A day with the button that clears it, what to know about it and, when the API would refuse it, why.</summary>
    private LinearLayout DateField(string caption, DateTime? value, Action<DateTime> set, string hint, string? problem, System.Windows.Input.ICommand clear, string clearLabel)
    {
        var shown = value is { } day ? ViewModels.Display.Date(DateOnly.FromDateTime(day)) : Strings.MemberPage_NotKnown;
        var field = Ui.Column(Ui.Picker(caption, shown, () => Ui.PickDate(value, set)));
        if (clear.CanExecute(null)) field.AddView(Ui.Button(clearLabel, () => Run(clear), filled: false), Ui.Params(wrap: true, top: 4));
        field.AddView(Ui.Text(hint, TextStyle.Caption), Ui.Params(top: 4));
        if (problem is not null) field.AddView(Ui.Text(problem, TextStyle.Caption, Tone.Danger), Ui.Params(top: 4));
        return field;
    }

    /// <summary>An entry of the belt dialog: the belt, and a tick on the one chosen in the form now.</summary>
    private View BeltChoice(BeltOption option, bool current)
    {
        var row = Ui.Row();
        if (!option.IsNone) row.AddView(Ui.BeltSwatch(option.Color, option.SecondaryColor, right: 8));
        row.AddView(Ui.Text(option.Label, current ? TextStyle.Heading : TextStyle.Body), Ui.Params(weight: 1));
        if (current) row.AddView(Ui.Text("✓", TextStyle.Heading), Ui.Params(wrap: true));
        return row;
    }

    /// <summary>Saves and, when that worked, goes back to the list, which says so. A refusal leaves the form open, with the reason.</summary>
    private async Task SaveAsync()
    {
        await _vm.SaveCommand.ExecuteAsync(null);
        if (_vm.IsFormOpen) return;

        _open = null;
        Invalidate();
    }

    private LinearLayout Field(string label, string value)
    {
        var row = Ui.Row(Ui.Text(label, TextStyle.Caption), Ui.Text(value, TextStyle.Body));
        row.GetChildAt(0)!.LayoutParameters = Ui.Params(weight: 1);
        row.GetChildAt(1)!.LayoutParameters = Ui.Params(wrap: true);
        row.SetMinimumHeight(Ui.Dp(36));
        return row;
    }

    /// <summary>The rows of the list; a list view builds only the ones on show.</summary>
    private sealed class MemberAdapter(Ui ui) : BaseAdapter<MemberRow>
    {
        public IReadOnlyList<MemberRow> Items
        {
            get;
            set
            {
                field = value;
                NotifyDataSetChanged();
            }
        } = [];

        public override int Count => Items.Count;

        public override MemberRow this[int position] => Items[position];

        public override long GetItemId(int position) => Items[position].Id;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var member = Items[position];
            var row = ui.Row();
            row.AddView(ui.Text(member.Name, TextStyle.Body), ui.Params(weight: 1));
            if (member.HasBelt)
            {
                row.AddView(ui.BeltSwatch(member.Belt.Color, member.Belt.SecondaryColor, right: 8));
                row.AddView(ui.Text(member.Belt.Label, TextStyle.Caption), ui.Params(wrap: true));
            }

            var card = ui.Card(row);
            card.SetPadding(ui.Dp(16), ui.Dp(12), ui.Dp(16), ui.Dp(12));
            var holder = new FrameLayout(ui.Context);
            holder.SetPadding(0, ui.Dp(4), 0, ui.Dp(4));
            holder.AddView(card);
            return holder;
        }
    }
}
