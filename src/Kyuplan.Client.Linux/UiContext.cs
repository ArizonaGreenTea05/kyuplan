using Kyuplan.Client.LinuxApp.Kit;

namespace Kyuplan.Client.LinuxApp;

/// <summary>
/// What a page needs from the window it is shown in: dialogs, toasts, the file chooser, the browser. Pages never hold the window itself, so they
/// do not care whether they sit in the main window or in a dialog.
/// </summary>
internal sealed class UiContext(Adw.ApplicationWindow window, Adw.ToastOverlay toasts, IServiceProvider services)
{
    public Adw.ApplicationWindow Window => window;

    public IServiceProvider Services => services;

    public T Get<T>() where T : notnull => (T)services.GetService(typeof(T))!;

    /// <summary>A short message at the bottom of the window that goes away by itself.</summary>
    public void Toast(string text) => toasts.AddToast(Adw.Toast.New(text));

    /// <summary>Asks a yes/no question in a dialog; <c>true</c> when the user chose <paramref name="confirm"/>.</summary>
    public Task<bool> ConfirmAsync(string heading, string? body, string confirm, string cancel, bool destructive = false)
    {
        var done = new TaskCompletionSource<bool>();
        var dialog = Adw.AlertDialog.New(heading, body);
        dialog.AddResponse("cancel", cancel);
        dialog.AddResponse("confirm", confirm);
        dialog.SetResponseAppearance("confirm", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
        dialog.DefaultResponse = "cancel";
        dialog.CloseResponse = "cancel";
        dialog.OnResponse += (_, e) => done.TrySetResult(e.Response == "confirm");
        dialog.Present(window);
        return done.Task;
    }

    /// <summary>Tells the user something (an error that has no place on the page).</summary>
    public void Inform(string heading, string? body, string close)
    {
        var dialog = Adw.AlertDialog.New(heading, body);
        dialog.AddResponse("close", close);
        dialog.Present(window);
    }

    /// <summary>Lets the user pick a file to read; <c>null</c> when they cancel.</summary>
    public async Task<Gio.File?> PickFileAsync(string title, params (string Name, string Pattern)[] filters)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.Title = title;
        SetFilters(dialog, filters);
        try
        {
            return await dialog.OpenAsync(window);
        }
        catch (GLib.GException)
        {
            return null; // dismissed
        }
    }

    /// <summary>Lets the user choose where to save a file; <c>null</c> when they cancel.</summary>
    public async Task<Gio.File?> SaveFileAsync(string title, string suggestedName)
    {
        var dialog = Gtk.FileDialog.New();
        dialog.Title = title;
        dialog.InitialName = suggestedName;
        try
        {
            return await dialog.SaveAsync(window);
        }
        catch (GLib.GException)
        {
            return null;
        }
    }

    private static void SetFilters(Gtk.FileDialog dialog, (string Name, string Pattern)[] filters)
    {
        if (filters.Length == 0) return;

        var list = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        foreach (var (name, pattern) in filters)
        {
            var filter = Gtk.FileFilter.New();
            filter.Name = name;
            foreach (var part in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries)) filter.AddPattern(part);
            list.Append(filter);
        }

        dialog.SetFilters(list);
    }

    /// <summary>Opens an address in the user's browser (or the program that handles it).</summary>
    public void OpenUri(Uri address)
    {
        if (address.Scheme is not ("http" or "https" or "mailto")) return;

        Gtk.UriLauncher.New(address.AbsoluteUri).LaunchAsync(window).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Lets the user choose where to keep a file the API made (an export, a backup) and writes it there; a file that cannot be written is reported.</summary>
    public async Task SaveAsync(Kyuplan.Client.Api.BackupFile file, string title)
    {
        if (await SaveFileAsync(title, file.FileName) is not { } target || target.GetPath() is not { } path) return;

        try
        {
            await File.WriteAllBytesAsync(path, file.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Inform(title, ex.Message, Kyuplan.Client.Resources.Strings.Comments_Cancel);
        }
    }
}
