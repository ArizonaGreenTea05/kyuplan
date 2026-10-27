using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>One permission as a tick box.</summary>
public sealed partial class PermissionRow : ObservableObject
{
    private readonly PermissionPickerViewModel _owner;

    private readonly Func<string>? _label;

    /// <param name="label">What the permission says, for a permission a plugin added (the texts of the program's own are in the language files).</param>
    internal PermissionRow(PermissionPickerViewModel owner, string key, Func<string>? label = null)
    {
        _owner = owner;
        Key = key;
        _label = label;
    }

    /// <summary>The key in <see cref="Permission"/>, e.g. <c>members.edit</c>.</summary>
    public string Key { get; }

    /// <summary>What the permission allows, in the user's language.</summary>
    public string Label => _label?.Invoke() ?? Strings.ResourceManager.GetString(Permission.ResourceKey(Key)) ?? Key;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.OnTicked(this);

    internal void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>The permissions of one domain (members, comments, ...), listed under its name.</summary>
public sealed partial class PermissionGroupRow : ObservableObject
{
    private readonly string _name;
    private readonly Func<string>? _label;

    internal PermissionGroupRow(PermissionPickerViewModel owner, PermissionGroup group)
    {
        _name = group.Name;
        Permissions = [.. group.Permissions.Select(key => new PermissionRow(owner, key))];
    }

    /// <summary>The permissions a plugin added, listed under the plugin's name.</summary>
    internal PermissionGroupRow(PermissionPickerViewModel owner, PluginDto plugin)
    {
        _name = plugin.Id;
        _label = () => PluginText.Pick(plugin.Name, plugin.NameDe);
        Permissions = [.. plugin.Permissions.Select(p => new PermissionRow(owner, p.Id, () => PluginText.Pick(p.Label, p.LabelDe)))];
    }

    internal bool IsPlugin => _label is not null;

    /// <summary>The domain, in the user's language.</summary>
    public string Label => _label?.Invoke() ?? Strings.ResourceManager.GetString("PermissionGroup_" + _name) ?? _name;

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

    private HashSet<string> _unknown = [];

    public PermissionPickerViewModel() => Groups = [.. Permission.Groups.Select(group => new PermissionGroupRow(this, group))];

    /// <summary>The domains with their permissions: the program's own, then one for each plugin that adds some (<see cref="SetPlugins"/>).</summary>
    public ObservableCollection<PermissionGroupRow> Groups { get; }

    /// <summary>
    /// The ticked permissions in the order of the catalogue. A permission that has no tick box here (it belongs to a plugin that is not installed) and was set stays
    /// in the list, so that saving a role does not take it away.
    /// </summary>
    public IReadOnlyList<string> Selected => [.. Groups.SelectMany(g => g.Permissions).Where(p => p.IsSelected).Select(p => p.Key), .. _unknown.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Offers the permissions the plugins that run added. What was ticked stays ticked; ticks of permissions that are no longer offered are kept as they are
    /// (see <see cref="Selected"/>).
    /// </summary>
    public void SetPlugins(PluginCatalogDto? catalog)
    {
        var selected = Selected.ToHashSet();
        foreach (var group in Groups.Where(g => g.IsPlugin).ToList()) Groups.Remove(group);
        foreach (var plugin in (catalog?.Plugins ?? []).Where(p => p.Permissions.Count > 0)) Groups.Add(new PermissionGroupRow(this, plugin));

        Set(selected);
        OnPropertyChanged(nameof(Selected));
    }

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
            _unknown = [.. wanted.Where(p => Groups.SelectMany(g => g.Permissions).All(r => r.Key != p) && PluginRules.IsPluginPermission(p))];
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
