using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One permission as a tick box.</summary>
public sealed partial class PermissionRow : ObservableObject
{
    private readonly PermissionPickerViewModel _owner;

    internal PermissionRow(PermissionPickerViewModel owner, string key)
    {
        _owner = owner;
        Key = key;
    }

    /// <summary>The key in <see cref="Permission"/>, e.g. <c>members.edit</c>.</summary>
    public string Key { get; }

    /// <summary>What the permission allows, in the user's language.</summary>
    public string Label => Strings.ResourceManager.GetString(Permission.ResourceKey(Key)) ?? Key;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.OnTicked(this);

    internal void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>The permissions of one domain (members, comments, ...), listed under its name.</summary>
public sealed partial class PermissionGroupRow : ObservableObject
{
    private readonly string _name;

    internal PermissionGroupRow(PermissionPickerViewModel owner, PermissionGroup group)
    {
        _name = group.Name;
        Permissions = [.. group.Permissions.Select(key => new PermissionRow(owner, key))];
    }

    /// <summary>The domain, in the user's language.</summary>
    public string Label => Strings.ResourceManager.GetString("PermissionGroup_" + _name) ?? _name;

    public IReadOnlyList<PermissionRow> Permissions { get; }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        foreach (var row in Permissions) row.Refresh();
    }
}

/// <summary>
/// Every permission there is, by domain, to tick. Used by the roles page and by the user form (a user's own permissions). A permission needs
/// the one that lets the user see the domain, so ticking one ticks the domain's <c>.view</c> as well, and unticking the <c>.view</c> unticks
/// the rest of the domain: nobody is given something to change in a place they may not look at. The <c>.viewOwn</c> permissions (reading what is
/// written about oneself) are the exception: they work without the <c>.view</c> of their domain and are not touched by it.
/// </summary>
public sealed partial class PermissionPickerViewModel : ObservableObject
{
    private bool _quiet;

    public PermissionPickerViewModel() => Groups = [.. Permission.Groups.Select(group => new PermissionGroupRow(this, group))];

    public IReadOnlyList<PermissionGroupRow> Groups { get; }

    /// <summary>The ticked permissions in the order of the catalogue.</summary>
    public IReadOnlyList<string> Selected => [.. Groups.SelectMany(g => g.Permissions).Where(p => p.IsSelected).Select(p => p.Key)];

    /// <summary>The ticks can be changed: the user may edit what is shown, and it is not the administrator role (which has them all).</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Raised when the user ticked or unticked something, not when <see cref="Set"/> fills the list.</summary>
    public event EventHandler? Changed;

    /// <summary>Ticks exactly these permissions, as they are (the dependencies between them are not applied: it is what is saved).</summary>
    public void Set(IEnumerable<string> permissions)
    {
        var wanted = permissions.ToHashSet();
        _quiet = true;
        try
        {
            foreach (var row in Groups.SelectMany(g => g.Permissions)) row.IsSelected = wanted.Contains(row.Key);
        }
        finally
        {
            _quiet = false;
        }
    }

    internal void OnTicked(PermissionRow row)
    {
        if (_quiet) return;

        _quiet = true;
        try
        {
            var group = Groups.First(g => g.Permissions.Contains(row));
            var view = group.Permissions[0].Key.EndsWith(".view", StringComparison.Ordinal) ? group.Permissions[0] : null;

            if (row.IsSelected && view is not null && !IsOwn(row)) view.IsSelected = true;
            if (!row.IsSelected && ReferenceEquals(row, view))
                foreach (var other in group.Permissions.Where(p => !IsOwn(p))) other.IsSelected = false;
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(nameof(Selected));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsOwn(PermissionRow row) => row.Key.EndsWith(".viewOwn", StringComparison.Ordinal);

    /// <summary>The language changed: the texts are read again.</summary>
    public void Refresh()
    {
        foreach (var group in Groups) group.Refresh();
    }
}
