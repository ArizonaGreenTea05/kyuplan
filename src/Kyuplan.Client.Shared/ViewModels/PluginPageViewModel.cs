using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// A page a plugin added. The plugin says in JSON what is on it (<see cref="PluginUiLayout"/>); the clients draw the <see cref="Items"/> and know nothing else
/// about it. Tables read endpoints of the plugin when the page is shown, buttons and forms call them; an action that asks for a confirmation (<see cref="PluginUiAction.Confirm"/>)
/// waits for the answer in <see cref="Confirmation"/>.
/// </summary>
public sealed partial class PluginPageViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private readonly PluginDto _plugin;
    private readonly PluginPageDto _page;
    private (PluginUiAction Action, JsonElement? Body)? _waiting;

    public PluginPageViewModel(ShellViewModel shell, IPlannerApi api, PluginDto plugin, PluginPageDto page) : base(shell)
    {
        _api = api;
        _plugin = plugin;
        _page = page;
        Items = [.. page.Layout.Elements.Select(Create).OfType<PluginItemViewModel>()];
    }

    private PluginItemViewModel? Create(PluginUiElement element) => element.Type switch
    {
        "heading" or "text" => new PluginTextViewModel(element),
        "table" => new PluginTableViewModel(element),
        "button" => new PluginButtonViewModel(element, RunAsync),
        "form" => new PluginFormBlockViewModel(element, RunAsync),
        _ => null,
    };

    /// <summary>The id of the plugin.</summary>
    public string PluginId => _plugin.Id;

    /// <summary>The id of the page, as the plugin names it.</summary>
    public string PageId => _page.Id;

    /// <summary>The key a client navigates by: <c>pluginId/pageId</c>.</summary>
    public string Key => $"{_plugin.Id}/{_page.Id}";

    /// <summary>The name of a symbol the page asks for in the navigation (<c>chart</c>); <c>null</c> = none. A client that does not know the name draws its default.</summary>
    public string? Icon => _page.Icon;

    public IReadOnlyList<PluginItemViewModel> Items { get; }

    protected override string? ViewPermission => _page.Permission;

    public override string Title => PluginText.Pick(_page.Title, _page.TitleDe);

    protected override bool ReloadsWithContext => false;

    protected override bool ReloadsOnActivation => true;

    /// <summary>The question an action asked, while it waits for the answer; empty otherwise.</summary>
    public string Confirmation => _waiting is { } waiting ? PluginText.Pick(waiting.Action.Confirm, waiting.Action.ConfirmDe) : "";

    public bool IsConfirming => _waiting is not null;

    /// <summary>What the last action said: that it is done, or its answer is not shown; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var item in Items) item.LanguageChanged();
        OnPropertyChanged(nameof(Confirmation));
    }

    protected override void OnPermissionsChanged()
    {
        if (!CanView)
        {
            foreach (var table in Items.OfType<PluginTableViewModel>()) table.Load(default);
        }

        base.OnPermissionsChanged();
    }

    protected override Task ReloadAsync() => LoadAsync(async ct =>
    {
        foreach (var table in Items.OfType<PluginTableViewModel>())
            table.Load(await _api.GetPluginDataAsync(_plugin.Id, table.SourcePath!, ct));
    });

    /// <summary>Calls the endpoint of an action (after the user said yes, if it asks), then reads the tables again.</summary>
    private async Task RunAsync(PluginUiAction action, JsonElement? body)
    {
        Notice = "";
        if (PluginText.PickOrNull(action.Confirm, action.ConfirmDe) is not null)
        {
            _waiting = (action, body);
            OnPropertyChanged(nameof(Confirmation));
            OnPropertyChanged(nameof(IsConfirming));
            return;
        }

        await SendAsync(action, body);
    }

    private async Task SendAsync(PluginUiAction action, JsonElement? body)
    {
        var done = false;
        await LoadAsync(async ct =>
        {
            await _api.SendPluginAsync(_plugin.Id, action.Method, action.Path, body, ct);
            foreach (var table in Items.OfType<PluginTableViewModel>())
                table.Load(await _api.GetPluginDataAsync(_plugin.Id, table.SourcePath!, ct));
            done = true;
        });
        if (done) Notice = Strings.Plugin_ActionDone;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_waiting is not var (action, body)) return;

        _waiting = null;
        OnPropertyChanged(nameof(Confirmation));
        OnPropertyChanged(nameof(IsConfirming));
        await SendAsync(action, body);
    }

    [RelayCommand]
    private void CancelConfirmation()
    {
        _waiting = null;
        OnPropertyChanged(nameof(Confirmation));
        OnPropertyChanged(nameof(IsConfirming));
    }

    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();
}
