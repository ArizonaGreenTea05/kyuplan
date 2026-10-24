using System.ComponentModel;

namespace Kyuplan.Client.LinuxApp.Kit;

/// <summary>
/// The image behind an address (what the API serves for a logo, SVG or bitmap), downloaded without blocking the window. There is no image until
/// it has arrived, and none when there is no address or it cannot be drawn: branding is cosmetic, so that is never an error.
/// </summary>
internal static class RemoteImage
{
    private const string SvgContentType = "image/svg+xml";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A picture that shows the image <paramref name="address"/> returns (<c>null</c>: nothing), again when the address changes.</summary>
    public static Gtk.Picture Picture(INotifyPropertyChanged source, Func<string?> address, int height, params string[] properties)
    {
        var picture = Gtk.Picture.New();
        picture.ContentFit = Gtk.ContentFit.Contain;
        picture.CanShrink = true;
        picture.HeightRequest = height;
        picture.WidthRequest = height;
        picture.Halign = Gtk.Align.Center;
        object? latest = null;

        async void Show()
        {
            var token = latest = new object();
            picture.SetPaintable(null);
            if (address() is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

            try
            {
                var paintable = await LoadAsync(uri);
                if (ReferenceEquals(latest, token)) picture.SetPaintable(paintable);
            }
            catch (Exception)
            {
                // async void: nothing may escape. The image stays out, as for an instance without a logo.
            }
        }

        Bind.Watch(picture, source, Show, properties);
        return picture;
    }

    /// <summary>Downloads and decodes the image; fails (any exception) when it cannot be downloaded or drawn.</summary>
    public static async Task<Gdk.Paintable> LoadAsync(Uri address, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        bool isSvg;
        if (address.Scheme == "data")
        {
            (bytes, isSvg) = ReadDataUri(address);
        }
        else
        {
            using var response = await Http.GetAsync(address, cancellationToken);
            response.EnsureSuccessStatusCode();
            isSvg = response.Content.Headers.ContentType?.MediaType == SvgContentType;
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        return Decode(bytes, isSvg);
    }

    /// <summary>The content of a <c>data:&lt;content type&gt;;base64,&lt;payload&gt;</c> address, as the view model makes them for a picked file.</summary>
    private static (byte[] Bytes, bool IsSvg) ReadDataUri(Uri address)
    {
        var text = address.OriginalString;
        var comma = text.IndexOf(',');
        var contentType = text["data:".Length..comma].Split(';')[0];
        return (Convert.FromBase64String(text[(comma + 1)..]), contentType == SvgContentType);
    }

    private static Gdk.Paintable Decode(byte[] bytes, bool isSvg)
    {
        if (isSvg && !SvgSafety.IsSelfContained(new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿')))
            throw new InvalidDataException("The SVG refers to something outside of the file.");

        var loader = GdkPixbuf.PixbufLoader.New();
        loader.Write(bytes);
        loader.Close();
        var pixbuf = loader.GetPixbuf() ?? throw new InvalidDataException("The image cannot be drawn.");
        return Gdk.Texture.NewForPixbuf(pixbuf);
    }
}
