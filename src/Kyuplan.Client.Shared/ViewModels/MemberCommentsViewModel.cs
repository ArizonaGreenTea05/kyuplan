using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>The number of comments on a member is known (after reading them, writing one or deleting one).</summary>
public sealed class CommentCountChangedEventArgs(int memberId, int count) : EventArgs
{
    public int MemberId { get; } = memberId;

    public int Count { get; } = count;
}

/// <summary>
/// The comments on one member, shown on the members page in place of the list: the threads (a comment and the replies to it, deepest last) and the
/// composer for a new comment, a reply or a change of a comment. The text is written in the markup of <see cref="CommentFormat"/>; the view
/// offers buttons for the styles (<see cref="ApplyFormat"/>) and shows the result as <see cref="Preview"/>. Tags are ticked in the composer and
/// can be made and changed there (<see cref="TagEditor"/>). What the user may do with comments is up to their permissions: write (new comments and
/// replies), edit (any comment; every edit is kept in its history) and delete (their own, or all). Used by both clients.
/// </summary>
public sealed partial class MemberCommentsViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private int? _replyingToId;
    private int? _editingId;

    public MemberCommentsViewModel(ShellViewModel shell, IPlannerApi api)
    {
        _shell = shell;
        _api = api;

        TagEditor = new TagEditorViewModel(shell, api, () => [.. TagOptions.Select(o => o.Tag)]);
        TagEditor.Saved += OnTagSaved;
        TagEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TagEditorViewModel.IsOpen) or nameof(TagEditorViewModel.Editing)) ComposerChanged();
        };

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) ComposerChanged();
        };
        shell.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(string.Empty);
            ComposerChanged();
        };
    }

    /// <summary>Whether the user may write comments and replies.</summary>
    public bool CanWriteComments => _shell.Can(Permission.CommentsWrite);

    /// <summary>Whether the user may change comments.</summary>
    public bool CanEditComments => _shell.Can(Permission.CommentsEdit);

    /// <summary>The composer is shown: to write, and for somebody who may only change comments while they change one.</summary>
    public bool CanCompose => CanWriteComments || IsEditing;

    /// <summary>The user may choose tags for a comment, which needs reading them; reading comments alone needs no tags.</summary>
    private bool UsesTags => CanWriteComments || CanEditComments;

    /// <summary>The user may make and change tags in the composer.</summary>
    public bool CanEditTags => _shell.Can(Permission.TagsEdit);

    /// <summary>Raised whenever the number of comments on the member shown is known.</summary>
    public event EventHandler<CommentCountChangedEventArgs>? CountChanged;

    /// <summary>The form that makes and changes tags, for the composer.</summary>
    public TagEditorViewModel TagEditor { get; }

    // --- The member and the threads --------------------------------------------------------------------------

    /// <summary>The member whose comments are shown; <c>null</c> while the page shows the list of members instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(Title))]
    private MemberRow? _member;

    public bool IsOpen => Member is not null;

    public string Title => Member is { } member ? string.Format(Strings.Comments_Title, member.Name) : "";

    /// <summary>
    /// Every comment on the member as one list: the threads, the newest thread first, each thread's comment followed by its replies, the
    /// oldest first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CountText))]
    private IReadOnlyList<CommentRow> _comments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    /// <summary>The comments were read and there are none: the page invites to write the first one.</summary>
    public bool IsEmpty => HasLoaded && Comments.Count == 0;

    /// <summary>"3 comments".</summary>
    public string CountText => Comments.Count == 1 ? Strings.Comments_CountOne : string.Format(Strings.Comments_CountMany, Comments.Count);

    /// <summary>Shows the comments on a member: reads them and the tags there are.</summary>
    public async Task ShowAsync(MemberRow member)
    {
        Close();
        Member = member;

        await LoadAsync(async ct =>
        {
            if (UsesTags) await ReadTagsAsync(member, ct);
            await ReadCommentsAsync(member, ct);
            HasLoaded = true;
        });
    }

    /// <summary>Back to the list of members: everything of the member is forgotten, a draft included.</summary>
    public void Close()
    {
        Member = null;
        Comments = [];
        TagOptions = [];
        HasLoaded = false;
        PendingDelete = null;
        ErrorMessage = null;
        TagEditor.CancelCommand.Execute(null);
        ResetComposer();
    }

    /// <summary>
    /// Reads the comments and the tags again, e.g. because the page is shown again and tags were changed on the tags page meanwhile. What is
    /// being written stays: a ticked tag that is gone is no longer ticked, and so is a comment that is gone no longer edited or replied to.
    /// </summary>
    public Task ReloadAsync() => RetryAsync();

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Member is not { } member) return;

        await LoadAsync(async ct =>
        {
            if (UsesTags) await ReadTagsAsync(member, ct);
            await ReadCommentsAsync(member, ct);
            HasLoaded = true;
        });
    }

    /// <summary>Reads the comments; a member that is no longer the one on show (the page went back to the list meanwhile) is left alone.</summary>
    private async Task ReadCommentsAsync(MemberRow member, CancellationToken cancellationToken)
    {
        var comments = await _api.GetCommentsAsync(member.Id, cancellationToken);
        if (!ReferenceEquals(Member, member)) return;

        Comments = Flatten(comments);
        CountChanged?.Invoke(this, new CommentCountChangedEventArgs(member.Id, comments.Count));

        // What the composer was about may be gone: somebody deleted the comment that is edited or replied to.
        if (_editingId is { } editing && Comments.All(c => c.Id != editing)) ResetComposer();
        if (_replyingToId is { } replying && Comments.All(c => c.Id != replying))
        {
            _replyingToId = null;
            ComposerChanged();
        }
    }

    private async Task ReadTagsAsync(MemberRow member, CancellationToken cancellationToken, int? alsoSelect = null)
    {
        var tags = await _api.GetTagsAsync(cancellationToken);
        if (!ReferenceEquals(Member, member)) return;

        var selected = TagOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet();
        if (alsoSelect is { } id) selected.Add(id);

        TagOptions = [.. tags.Select(t => new TagOption(t) { IsSelected = selected.Contains(t.Id) })];
    }

    /// <summary>The threads as one list: the newest thread first; within a thread a comment is followed by its replies, the oldest first.</summary>
    private List<CommentRow> Flatten(IReadOnlyList<CommentDto> comments)
    {
        var ids = comments.Select(c => c.Id).ToHashSet();
        var replies = comments.Where(c => c.ParentId is { } p && ids.Contains(p)).ToLookup(c => c.ParentId!.Value);
        var user = _shell.CurrentUser;
        List<CommentRow> rows = [];

        int Descendants(int id) => replies[id].Sum(reply => 1 + Descendants(reply.Id));

        void Add(CommentDto comment, int depth)
        {
            var mayDelete = user is not null && (_shell.Can(Permission.CommentsDeleteAny) || (user.Id == comment.AuthorId && _shell.Can(Permission.CommentsDelete)));
            rows.Add(new CommentRow(comment, depth, Descendants(comment.Id), mayDelete));
            foreach (var reply in replies[comment.Id].OrderBy(r => r.Id)) Add(reply, depth + 1);
        }

        foreach (var root in comments.Where(c => c.ParentId is not { } p || !ids.Contains(p)).OrderByDescending(c => c.Id)) Add(root, 0);
        return rows;
    }

    // --- The composer ----------------------------------------------------------------------------------------

    /// <summary>What is written, in the markup of <see cref="CommentFormat"/>. A view with a text box keeps it in step with the box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasPreview), nameof(TextProblem))]
    private string _text = "";

    /// <summary>The tags there are, each ticked when the comment being written carries it.</summary>
    [ObservableProperty]
    private IReadOnlyList<TagOption> _tagOptions = [];

    /// <summary>The comment being replied to; <c>null</c> when none is.</summary>
    public CommentRow? ReplyingTo => Comments.FirstOrDefault(c => c.Id == _replyingToId);

    /// <summary>The comment being edited; <c>null</c> when none is.</summary>
    public CommentRow? EditingRow => Comments.FirstOrDefault(c => c.Id == _editingId);

    public bool IsReplying => ReplyingTo is not null;

    public bool IsEditing => EditingRow is not null;

    /// <summary>"Write a comment", "Reply to Anna" or "Edit comment".</summary>
    public string ComposerTitle =>
        EditingRow is not null ? Strings.Comments_ComposerEdit
        : ReplyingTo is { } parent ? string.Format(Strings.Comments_ComposerReply, parent.AuthorName)
        : Strings.Comments_ComposerNew;

    /// <summary>What the button that saves says.</summary>
    public string SaveText => EditingRow is not null ? Strings.Comments_SaveEdit : IsReplying ? Strings.Comments_PostReply : Strings.Comments_Post;

    /// <summary>The text as the comment will show it, styled.</summary>
    public IReadOnlyList<CommentSpan> Preview => CommentFormat.Parse(CommentFormat.Clean(Text));

    public bool HasPreview => Preview.Count > 0;

    public string? TextProblem => Text.Length > CommentRules.MaxBodyLength ? string.Format(Strings.Comments_TooLong, CommentRules.MaxBodyLength) : null;

    /// <summary>The ids of the ticked tags.</summary>
    private List<int> SelectedTagIds => [.. TagOptions.Where(o => o.IsSelected).Select(o => o.Id).Order()];

    /// <summary>The comment has words the API accepts and, when it is an edit, something differs from what the comment has.</summary>
    public bool CanSave
    {
        get
        {
            if (!IsOpen || IsBusy) return false;

            var body = CommentFormat.Clean(Text);
            if (CommentRules.CheckBody(body) is not null) return false;
            if (EditingRow is not { } editing) return CanWriteComments;
            if (!CanEditComments) return false;

            return body != editing.Comment.Body || !SelectedTagIds.SequenceEqual(editing.Comment.Tags.Select(t => t.Id).Order());
        }
    }

    /// <summary>Only a reply or an edit can be given up; a new comment that is not written is just not sent.</summary>
    private bool CanCancelComposer() => _replyingToId is not null || _editingId is not null;

    [RelayCommand(CanExecute = nameof(CanCancelComposer))]
    private void CancelComposer()
    {
        ErrorMessage = null;
        ResetComposer();
    }

    private bool CanReply(CommentRow? comment) => CanWriteComments && comment is not null && comment.Id != _replyingToId;

    [RelayCommand(CanExecute = nameof(CanReply))]
    private void Reply(CommentRow comment)
    {
        if (!CanReply(comment)) return; // Execute does not ask CanExecute: what the user may not do is not done

        ErrorMessage = null;
        if (_editingId is not null) ClearDraft(); // the text and the tags were the edited comment's, not a draft to reply with
        _editingId = null;
        _replyingToId = comment.Id;
        ComposerChanged();
    }

    private bool CanEdit(CommentRow? comment) => CanEditComments && comment is not null && comment.Id != _editingId;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit(CommentRow comment)
    {
        if (!CanEdit(comment)) return;

        ErrorMessage = null;
        _replyingToId = null;
        _editingId = comment.Id;

        var carried = comment.Comment.Tags.Select(t => t.Id).ToHashSet();
        foreach (var option in TagOptions) option.IsSelected = carried.Contains(option.Id);
        Text = comment.Comment.Body;
        ComposerChanged();
    }

    private void ResetComposer()
    {
        _replyingToId = null;
        _editingId = null;
        ClearDraft();
        ComposerChanged();
    }

    private void ClearDraft()
    {
        Text = "";
        foreach (var option in TagOptions) option.IsSelected = false;
    }

    /// <summary>
    /// What a button of the toolbar does: puts a style on the part of the text that is selected, or takes it off again. The view says what is
    /// selected, and selects what the answer says afterwards.
    /// </summary>
    public TextChange ApplyFormat(CommentStyle style, int selectionStart, int selectionLength)
    {
        var change = CommentFormat.Toggle(Text, selectionStart, selectionLength, style);
        Text = change.Text;
        return change;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || Member is not { } member) return; // a command is only guarded by its button

        var body = CommentFormat.Clean(Text);
        var tagIds = SelectedTagIds;
        var editing = EditingRow;
        var replyTo = _replyingToId;

        var saved = false;
        await LoadAsync(async ct =>
        {
            if (editing is null)
                await _api.CreateCommentAsync(member.Id, new CommentInputDto(body, tagIds, replyTo), ct);
            else
                await _api.UpdateCommentAsync(editing.Id, new CommentUpdateDto(body, tagIds), ct);

            saved = true;
            await ReadCommentsAsync(member, ct);
        });

        // It is saved, even if showing the new list failed: the composer must not be left to save it a second time.
        if (saved) ResetComposer();
    }

    partial void OnTextChanged(string value)
    {
        if (IsOpen) ErrorMessage = null; // the user is correcting the comment: what the API said about the last attempt is old news
        ComposerChanged();
    }

    partial void OnTagOptionsChanged(IReadOnlyList<TagOption>? oldValue, IReadOnlyList<TagOption> newValue)
    {
        foreach (var option in oldValue ?? []) option.PropertyChanged -= OnOptionChanged;
        foreach (var option in newValue) option.PropertyChanged += OnOptionChanged;

        TagEditor.KnownTagsChanged();
        ComposerChanged();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagOption.IsSelected)) ComposerChanged();
    }

    partial void OnCommentsChanged(IReadOnlyList<CommentRow> value) => ComposerChanged();

    private void ComposerChanged()
    {
        OnPropertyChanged(nameof(ReplyingTo));
        OnPropertyChanged(nameof(EditingRow));
        OnPropertyChanged(nameof(IsReplying));
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(ComposerTitle));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
        CancelComposerCommand.NotifyCanExecuteChanged();
        ReplyCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        AskDeleteCommand.NotifyCanExecuteChanged();
        NewTagCommand.NotifyCanExecuteChanged();
        EditTagCommand.NotifyCanExecuteChanged();
    }

    // --- Tags in the composer --------------------------------------------------------------------------------

    private bool CanNewTag() => CanEditTags && !(TagEditor.IsOpen && TagEditor.Editing is null);

    [RelayCommand(CanExecute = nameof(CanNewTag))]
    private Task NewTagAsync() => TagEditor.BeginCreateAsync();

    private bool CanEditTag(TagOption? tag) => CanEditTags && tag is not null && !(TagEditor.IsOpen && TagEditor.Editing?.Id == tag.Id);

    [RelayCommand(CanExecute = nameof(CanEditTag))]
    private Task EditTagAsync(TagOption tag) => TagEditor.BeginEditAsync(tag.Tag);

    /// <summary>A tag was made or changed in the composer: the list is read again, a new tag comes ticked, and a changed one shows its new name and event on the comments.</summary>
    private async void OnTagSaved(object? sender, TagSavedEventArgs e)
    {
        if (Member is not { } member) return;

        await LoadAsync(async ct =>
        {
            await ReadTagsAsync(member, ct, alsoSelect: e.Created ? e.Tag.Id : null);
            if (!e.Created) await ReadCommentsAsync(member, ct);
        });
    }

    // --- History ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private static void ToggleHistory(CommentRow comment) => comment.IsHistoryOpen = !comment.IsHistoryOpen;

    // --- Deleting --------------------------------------------------------------------------------------------

    /// <summary>The comment waiting for the confirmation that deletes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDelete), nameof(ConfirmText))]
    private CommentRow? _pendingDelete;

    public bool IsConfirmingDelete => PendingDelete is not null;

    partial void OnPendingDeleteChanged(CommentRow? value) => AskDeleteCommand.NotifyCanExecuteChanged();

    /// <summary>"Delete this comment and its 2 replies?"</summary>
    public string ConfirmText => PendingDelete switch
    {
        null => "",
        { ReplyCount: 0 } => Strings.Comments_ConfirmDelete,
        { ReplyCount: 1 } => Strings.Comments_ConfirmDeleteReply,
        var comment => string.Format(Strings.Comments_ConfirmDeleteReplies, comment.ReplyCount),
    };

    private bool CanAskDelete(CommentRow? comment) => comment is { CanDelete: true } && !IsBusy && !ReferenceEquals(comment, PendingDelete);

    [RelayCommand(CanExecute = nameof(CanAskDelete))]
    private void AskDelete(CommentRow comment)
    {
        if (!CanAskDelete(comment)) return;

        ErrorMessage = null;
        PendingDelete = comment;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } comment || Member is not { } member) return;

        PendingDelete = null;
        await LoadAsync(async ct =>
        {
            await _api.DeleteCommentAsync(comment.Id, ct);
            await ReadCommentsAsync(member, ct);
        });
    }

    // --- Language --------------------------------------------------------------------------------------------

    /// <summary>The language changed: every text built from data is built again.</summary>
    public void Refresh()
    {
        foreach (var comment in Comments) comment.Refresh();
        foreach (var option in TagOptions) option.Refresh();
        TagEditor.Refresh();

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ComposerTitle));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(TextProblem));
    }
}
