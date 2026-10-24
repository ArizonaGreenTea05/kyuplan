using System.Collections.Specialized;
using System.ComponentModel;
using Android.Views;
using Android.Widget;

namespace Kyuplan.Client.AndroidApp.Kit;

/// <summary>
/// A screen of the app: a view that draws a view model. It watches the view models it was told to (<see cref="Observe(INotifyPropertyChanged, string[])"/>)
/// and draws itself again, on the UI thread and once for a burst of changes, when one of them reports a change.
/// </summary>
public abstract class Screen(Ui ui) : IDisposable
{
    private readonly List<Action> _unsubscribe = [];
    private bool _pending;
    private bool _disposed;

    protected Ui Ui { get; } = ui;

    public abstract View Root { get; }

    /// <summary>Draws the screen from the view models. Called once by <see cref="Start"/>, then whenever something watched changed.</summary>
    protected abstract void Render();

    /// <summary>Draws the screen for the first time. Not the constructor's job: the derived class has to be set up first.</summary>
    public void Start() => Render();

    /// <summary>The user pressed back: <c>true</c> when the screen used it (it closed a detail view), <c>false</c> when it is at its top.</summary>
    public virtual bool HandleBack() => false;

    /// <summary>
    /// Watches <paramref name="source"/>. Without <paramref name="names"/> every change counts, otherwise only these properties (a view model
    /// that says "everything changed", with an empty name, always counts).
    /// </summary>
    protected void Observe(INotifyPropertyChanged source, params string[] names)
    {
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (names.Length == 0 || string.IsNullOrEmpty(e.PropertyName) || names.Contains(e.PropertyName)) Invalidate();
        }

        source.PropertyChanged += Handler;
        _unsubscribe.Add(() => source.PropertyChanged -= Handler);
    }

    protected void ObserveCollection(INotifyCollectionChanged source)
    {
        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => Invalidate();

        source.CollectionChanged += Handler;
        _unsubscribe.Add(() => source.CollectionChanged -= Handler);
    }

    /// <summary>Another thing to run when the screen is let go of (an event of something that is not a view model).</summary>
    protected void OnDispose(Action action) => _unsubscribe.Add(action);

    protected void Invalidate()
    {
        if (_pending || _disposed) return;

        _pending = true;
        Ui.Post(() =>
        {
            _pending = false;
            if (!_disposed) Render();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var action in _unsubscribe) action();
        _unsubscribe.Clear();
    }
}

/// <summary>A screen that is a column in a scroll view and is rebuilt from its view model as a whole. It keeps the scroll position meanwhile.</summary>
public abstract class ScrollScreen : Screen
{
    private readonly ScrollView _scroll;
    private readonly LinearLayout _content;
    private bool _toTop;

    protected ScrollScreen(Ui ui) : base(ui)
    {
        _content = new LinearLayout(ui.Context) { Orientation = Orientation.Vertical };
        _content.SetPadding(ui.Dp(16), ui.Dp(8), ui.Dp(16), ui.Dp(24));
        _scroll = new ScrollView(ui.Context) { FillViewport = true };
        _scroll.AddView(_content, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    public override View Root => _scroll;

    /// <summary>Puts the page into <paramref name="content"/>. Anything added before is gone.</summary>
    protected abstract void Build(LinearLayout content);

    /// <summary>The next drawing starts at the top, for a page that shows something else now (a detail view opened or closed).</summary>
    protected void ScrollToTop() => _toTop = true;

    protected override void Render()
    {
        var position = _toTop ? 0 : _scroll.ScrollY;
        _toTop = false;
        _content.RemoveAllViews();
        Build(_content);
        _scroll.Post(() => _scroll.ScrollTo(0, position));
    }
}
