using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg.Skia;
using Kyuplan.Client;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// Downloads an image the API serves (a logo, an icon) and turns it into something WPF can draw, off the UI thread. WPF itself draws
/// bitmaps only, so an SVG, which is what the instances' logos usually are, is rendered to a bitmap here (Skia, the engine of the
/// browsers, so clipped and transformed artwork comes out as it does there). A bitmap that is large enough is sharp at any size the
/// window shows it in; the result is frozen and can be shared.
/// </summary>
public static class ImageLoader
{
    private const string SvgContentType = "image/svg+xml";

    /// <summary>The longer side of a rendered SVG, in pixels: sharp up to far more than the logo's place in the window.</summary>
    private const int SvgPixels = 1024;

    /// <summary>The sizes an icon is drawn in: what Windows asks for at 100% to 250% scaling (16 to 40 for the title bar and taskbar, 32 to 64 for Alt+Tab), plus larger ones for the shell.</summary>
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly byte[] IcoSignature = [0x00, 0x00, 0x01, 0x00];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Skia keeps global state (fonts); two logos are rendered at once on start-up, so one at a time.
    private static readonly object SvgGate = new();

    /// <summary>
    /// The image behind the address. Fails (any exception) when it cannot be downloaded or drawn: a missing logo is no reason to stop
    /// the program, so callers are expected to leave the image out then.
    /// </summary>
    public static async Task<ImageSource> LoadAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var (bytes, isSvg) = await DownloadAsync(address, cancellationToken);
        return await Task.Run(() => isSvg ? DrawSvg(bytes) : DecodeBitmap(bytes), cancellationToken);
    }

    /// <summary>
    /// Like <see cref="LoadAsync"/>, for an image that becomes the icon of a window. Windows shows that at 16 to 64 pixels, and WPF shrinks
    /// a single big bitmap to that size without averaging, which leaves it jagged. So the result is a set of images in the sizes
    /// Windows asks for (an ICO, of which WPF picks the best fit): the ones of an uploaded ICO as they are, any other image drawn anew
    /// at each size (an SVG from the vector artwork). Call it from the UI thread: the result is not frozen, as a frame keeps the decoder
    /// it was read with, and WPF reads that decoder when it makes the icon, so both must belong to the thread of the window.
    /// </summary>
    public static async Task<ImageSource> LoadIconAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var (bytes, isSvg) = await DownloadAsync(address, cancellationToken);
        var ico = await Task.Run(() => bytes.AsSpan().StartsWith(IcoSignature) ? bytes : BuildIco(bytes, isSvg), cancellationToken);
        return BitmapFrame.Create(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    private static async Task<(byte[] Bytes, bool IsSvg)> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        if (address.Scheme == "data") return ReadDataUri(address); // a file that was picked and is not saved yet

        using var response = await Http.GetAsync(address, cancellationToken);
        response.EnsureSuccessStatusCode();

        var isSvg = response.Content.Headers.ContentType?.MediaType == SvgContentType;
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), isSvg);
    }

    /// <summary>The content of a <c>data:&lt;content type&gt;;base64,&lt;payload&gt;</c> address, as the view model makes them for a picked file.</summary>
    private static (byte[] Bytes, bool IsSvg) ReadDataUri(Uri address)
    {
        var text = address.OriginalString;
        var comma = text.IndexOf(',');
        var contentType = text["data:".Length..comma].Split(';')[0];
        return (Convert.FromBase64String(text[(comma + 1)..]), contentType == SvgContentType);
    }

    private static BitmapImage DecodeBitmap(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // decodes now, so the stream is not needed afterwards
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource DrawSvg(byte[] bytes)
    {
        var (width, height, pixels) = WithSvg(bytes, (picture, bounds) =>
        {
            var scale = SvgPixels / Math.Max(bounds.Width, bounds.Height);
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            return (width, height, bitmap.GetPixelSpan().ToArray());
        });

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>Parses an SVG that is self-contained and has something to draw, and hands its picture and bounds to <paramref name="draw"/>.</summary>
    private static T WithSvg<T>(byte[] bytes, Func<SKPicture, SKRect, T> draw)
    {
        var text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        if (!SvgSafety.IsSelfContained(text)) throw new InvalidDataException("The SVG refers to something outside of the file.");

        lock (SvgGate)
        {
            using var svg = new SKSvg();
            var picture = svg.FromSvg(text);
            var bounds = picture?.CullRect ?? default;
            if (picture is null || bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidDataException("The SVG has nothing to draw.");

            return draw(picture, bounds);
        }
    }

    /// <summary>An ICO with the image drawn at every size of <see cref="IconSizes"/>, each stored as a PNG (which Windows reads since Vista).</summary>
    private static byte[] BuildIco(byte[] bytes, bool isSvg)
    {
        var images = isSvg ? DrawSvgIcons(bytes) : DrawBitmapIcons(bytes);

        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            var side = (byte)(size >= 256 ? 0 : size); // 0 stands for 256
            writer.Write(side);
            writer.Write(side);
            writer.Write((byte)0); // palette
            writer.Write((byte)0);
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images) writer.Write(png);
        writer.Flush();
        return ico.ToArray();
    }

    // The artwork is fitted into a square and centred, as an icon is square whatever the image is.
    private static List<(int Size, byte[] Png)> DrawSvgIcons(byte[] bytes) =>
        WithSvg(bytes, (picture, bounds) => IconSizes.Select(size => (size, DrawPng(size, canvas =>
        {
            var scale = size / Math.Max(bounds.Width, bounds.Height);
            canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }))).ToList());

    private static List<(int Size, byte[] Png)> DrawBitmapIcons(byte[] bytes)
    {
        using var source = SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("The file is not an image.");
        return IconSizes.Select(size => (size, DrawPng(size, canvas =>
        {
            var scale = size / (float)Math.Max(source.Width, source.Height);
            float width = source.Width * scale, height = source.Height * scale;
            canvas.DrawImage(source, SKRect.Create((size - width) / 2, (size - height) / 2, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)); // averages when it shrinks
        }))).ToList();
    }

    private static byte[] DrawPng(int size, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
