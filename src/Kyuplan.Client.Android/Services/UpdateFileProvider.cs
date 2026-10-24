using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace Kyuplan.Client.AndroidApp.Services;

/// <summary>
/// Hands the package installer the update that was downloaded into the cache folder: the installer is another app, which can only read it
/// through a content address that this app grants it for that one start. Serves the .apk files of the cache folder and nothing else.
/// </summary>
[ContentProvider([Authority], Exported = false, GrantUriPermissions = true)]
public sealed class UpdateFileProvider : ContentProvider
{
    /// <summary>The application id and ".updates".</summary>
    public const string Authority = "org.kyuplan.client.updates";

    private const string ApkType = "application/vnd.android.package-archive";

    /// <summary>The address under which the installer can read <paramref name="file"/>; a file outside the cache folder is copied into it first.</summary>
    public static Uri UriFor(Context context, string file)
    {
        var name = System.IO.Path.GetFileName(file);
        var target = new Java.IO.File(context.CacheDir, name);
        if (!string.Equals(target.AbsolutePath, file, StringComparison.Ordinal)) System.IO.File.Copy(file, target.AbsolutePath, overwrite: true);

        return Uri.Parse($"content://{Authority}/{System.Uri.EscapeDataString(name)}")!;
    }

    public override bool OnCreate() => true;

    public override string? GetType(Uri uri) => ApkType;

    public override ParcelFileDescriptor? OpenFile(Uri uri, string mode)
    {
        if (!mode.StartsWith('r')) throw new Java.Lang.SecurityException("read only");

        return ParcelFileDescriptor.Open(Resolve(uri), ParcelFileMode.ReadOnly);
    }

    public override ICursor? Query(Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
    {
        var file = Resolve(uri);
        var cursor = new MatrixCursor([IOpenableColumns.DisplayName, IOpenableColumns.Size]);
        cursor.AddRow([file.Name!, file.Length()]);
        return cursor;
    }

    public override Uri? Insert(Uri uri, ContentValues? values) => throw new Java.Lang.UnsupportedOperationException();

    public override int Delete(Uri uri, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();

    public override int Update(Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new Java.Lang.UnsupportedOperationException();

    /// <summary>The .apk file of the cache folder that <paramref name="uri"/> names; anything else (another folder, another kind of file) is refused.</summary>
    private Java.IO.File Resolve(Uri uri)
    {
        var name = uri.LastPathSegment;
        var file = new Java.IO.File(Context!.CacheDir, name ?? "");
        if (name is null || !name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || file.CanonicalFile.Parent != Context.CacheDir!.CanonicalPath || !file.Exists())
            throw new Java.Lang.SecurityException("not an update");

        return file;
    }
}
