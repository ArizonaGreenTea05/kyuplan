using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>
/// The glue between the shared view models and GTK widgets. GTK has no data binding, so a widget that shows a property subscribes to the
/// view model's <see cref="INotifyPropertyChanged"/> and sets itself again; the subscription ends with the widget (its <c>destroy</c> signal),
/// so the view models, which live as long as the program, never keep a dead page alive.
/// </summary>
internal static class Bind
{
    /// <summary>Runs <paramref name="update"/> now and whenever <paramref name="source"/> says one of <paramref name="properties"/> (any, if none are named) changed.</summary>
    public static void Watch(Gtk.Widget owner, INotifyPropertyChanged source, Action update, params string[] properties)
    {
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (properties.Length == 0 || string.IsNullOrEmpty(e.PropertyName) || Array.IndexOf(properties, e.PropertyName) >= 0) update();
        }

        source.PropertyChanged += Handler;
        owner.OnDestroy += (_, _) => source.PropertyChanged -= Handler;
        update();
    }

    /// <summary>Runs <paramref name="update"/> whenever the items of <paramref name="source"/> (when it is an observable collection) change.</summary>
    public static void WatchItems(Gtk.Widget owner, object? source, Action update)
    {
        if (source is not INotifyCollectionChanged observable) return;

        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => update();
        observable.CollectionChanged += Handler;
        owner.OnDestroy += (_, _) => observable.CollectionChanged -= Handler;
    }

    /// <summary>Calls <paramref name="action"/> whenever the GObject property <paramref name="name"/> of <paramref name="widget"/> changes (the GTK side of a two-way binding).</summary>
    public static void OnProperty(GObject.Object widget, string name, Action action) =>
        widget.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == name) action();
        };

    private static PropertyInfo Property(object source, string name) =>
        source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new ArgumentException($"{source.GetType().Name} has no property {name}.");

    public static T Get<T>(object source, string name) => Convert<T>(Property(source, name).GetValue(source));

    public static object? GetRaw(object source, string name) => Property(source, name).GetValue(source);

    /// <summary>Writes a property that has a setter; a value that is already there is not written again (no round trips between widget and view model).</summary>
    public static void Set(object source, string name, object? value)
    {
        var property = Property(source, name);
        if (!property.CanWrite) return;

        var target = property.PropertyType;
        var converted = value is null || target.IsInstanceOfType(value) ? value : System.Convert.ChangeType(value, Nullable.GetUnderlyingType(target) ?? target);
        if (!Equals(property.GetValue(source), converted)) property.SetValue(source, converted);
    }

    private static T Convert<T>(object? value) => value switch
    {
        null => default!,
        T typed => typed,
        _ => (T)System.Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)),
    };

    /// <summary>The elements of an untyped sequence as <typeparamref name="T"/>.</summary>
    public static IEnumerable<T> Items<T>(object? value) => value is IEnumerable sequence ? sequence.Cast<T>() : [];
}
