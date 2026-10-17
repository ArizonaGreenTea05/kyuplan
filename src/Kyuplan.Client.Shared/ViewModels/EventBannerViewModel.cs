using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>An event that is going on, as the banner shows it.</summary>
public sealed class BannerEvent(CalendarEventDto calendarEvent) : ObservableObject
{
    public CalendarEventDto Event => calendarEvent;

    /// <summary>"Belt exam is in progress until 19:30".</summary>
    public string Text => string.Format(Strings.Banner_InProgress, calendarEvent.Name, Display.Time(calendarEvent.End));

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The banner at the top of every page while an event of the selected group is going on: it links to the event's detail page. It asks the API
/// every <see cref="DefaultInterval"/> once <see cref="Watch"/> was called, and whenever the group or the permissions of the user change. It
/// is decoration: a failed request leaves what is shown as it is and is never reported. A client shows the event when
/// <see cref="OpenRequested"/> is raised.
/// </summary>
public sealed partial class EventBannerViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly ShellViewModel _shell;
    private readonly IPlannerApi _api;
    private CancellationTokenSource? _watching;
    private int _generation;

    public EventBannerViewModel(ShellViewModel shell, IPlannerApi api)
    {
        _shell = shell;
        _api = api;

        shell.ContextChanged += (_, _) => _ = RefreshAsync();
        shell.PermissionsChanged += (_, _) => _ = RefreshAsync();
        shell.Language.Changed += (_, _) =>
        {
            foreach (var e in Events) e.Refresh();
        };
    }

    /// <summary>The events going on, the one that started first at the top.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvents))]
    private IReadOnlyList<BannerEvent> _events = [];

    public bool HasEvents => Events.Count > 0;

    /// <summary>The user asked to see an event: the client navigates to its detail page.</summary>
    public event EventHandler<CalendarEventDto>? OpenRequested;

    [RelayCommand]
    private void Open(BannerEvent? banner)
    {
        if (banner is not null) OpenRequested?.Invoke(this, banner.Event);
    }

    /// <summary>Asks the API what is going on now. Never throws.</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        var groupId = _shell.SelectedGroupId;
        if (!_shell.Can(Permission.CalendarView) || string.IsNullOrEmpty(groupId))
        {
            Events = [];
            return;
        }

        try
        {
            var current = await _api.GetCurrentEventsAsync(groupId);
            if (generation != _generation) return; // the group changed meanwhile: that answer is for another one
            if (Events.Select(e => e.Event).SequenceEqual(current)) return;

            Events = [.. current.OrderBy(e => e.Start).Select(e => new BannerEvent(e))];
        }
        catch (Exception ex) when (ex is PlannerApiException or HttpRequestException or OperationCanceledException)
        {
            // decoration: keep what is shown
        }
    }

    /// <summary>Starts asking every <paramref name="every"/> for as long as this lives. Call it on the thread that owns the bound controls.</summary>
    public void Watch(TimeSpan? every = null)
    {
        if (_watching is not null) return;

        _watching = new CancellationTokenSource();
        _ = WatchAsync(every ?? DefaultInterval, _watching.Token);
    }

    private async Task WatchAsync(TimeSpan every, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            await RefreshAsync();
            while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    public void Dispose()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;
    }
}
