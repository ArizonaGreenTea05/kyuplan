using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// The GitHub theme packs administrators added (<see cref="ThemeSourceDto"/>): their definitions and how the last read went are in
/// <c>sources/index.json</c> of the themes folder (<see cref="ThemePackStore"/>), the themes each one offers in <c>sources/&lt;key&gt;.json</c>. The
/// catalog follows these files by itself; a read that finds nothing new leaves them alone, so it does not make the clients load the catalog again.
/// </summary>
public sealed class ThemeSourceStore(ThemePackStore packs, TimeProvider time, ILogger<ThemeSourceStore> log)
{
    private readonly Lock _lock = new();

    private sealed record Stored(string Id, string Owner, string Repo, int ThemeCount, DateTimeOffset? LastRunUtc, bool? LastSucceeded, string? LastMessage);

    /// <summary>The key of a repository (case does not matter to GitHub): six hex digits, the start of the ids of its themes.</summary>
    public static string KeyOf(string owner, string repo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}/{repo}".ToLowerInvariant())))[..6].ToLowerInvariant();

    private string IndexPath => Path.Combine(packs.Directory, ThemePackStore.SourcesFolder, ThemePackStore.SourcesIndex);

    private string PacksPath(string id) => Path.Combine(packs.Directory, ThemePackStore.SourcesFolder, id + ".json");

    public IReadOnlyList<ThemeSourceDto> List()
    {
        lock (_lock) return [.. Load().Select(ToDto)];
    }

    public ThemeSourceDto? Find(string id)
    {
        lock (_lock) return Load().FirstOrDefault(s => s.Id == id) is { } found ? ToDto(found) : null;
    }

    /// <summary>Adds the repository; its themes come with the first read (<see cref="ThemeSourceSync"/>). Throws <see cref="InvalidInputException"/> for an address that is none or a pack that is there already.</summary>
    public ThemeSourceDto Add(string? url)
    {
        if (ThemeSourceRules.Parse(url) is not var (owner, repo))
            throw new InvalidInputException("url must be the address of a public GitHub repository, like https://github.com/owner/repo.");

        lock (_lock)
        {
            var all = Load();
            if (all.Count >= ThemeSourceRules.MaxSources) throw new InvalidInputException($"At most {ThemeSourceRules.MaxSources} theme packs can be added; remove one first.");

            var id = KeyOf(owner, repo);
            if (all.Any(s => s.Id == id)) throw new InvalidInputException($"The theme pack '{owner}/{repo}' is added already.");

            var created = new Stored(id, owner, repo, 0, null, null, null);
            Save([.. all, created]);
            return ToDto(created);
        }
    }

    /// <summary>Removes the pack and its themes. <c>false</c> when there is no such pack.</summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.All(s => s.Id != id)) return false;

            Save([.. all.Where(s => s.Id != id)]);
            File.Delete(PacksPath(id));
            return true;
        }
    }

    /// <summary>
    /// Remembers how a read went. <paramref name="offered"/> is what the pack offers from now on; <c>null</c> keeps the themes it offered so far
    /// (a read that failed). The themes are written only when they changed.
    /// </summary>
    public void Record(string id, IReadOnlyList<ThemePackDto>? offered, bool succeeded, string? message)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.FirstOrDefault(s => s.Id == id) is not { } existing) return; // removed while it was read

            if (offered is not null) WritePacks(id, offered);
            var changed = existing with
            {
                ThemeCount = offered?.Count ?? existing.ThemeCount, LastRunUtc = time.GetUtcNow(), LastSucceeded = succeeded, LastMessage = message,
            };
            Save([.. all.Select(s => s.Id == id ? changed : s)]);
        }
    }

    /// <summary>The packs that were never read, or whose time is over (sooner after a failure).</summary>
    public IReadOnlyList<ThemeSourceDto> Due()
    {
        var now = time.GetUtcNow();
        return [.. List().Where(s => s.LastSyncUtc is not { } last || now >= last + (s.LastSucceeded == true ? ThemeSourceRules.RefreshInterval : ThemeSourceRules.RetryInterval))];
    }

    private void WritePacks(string id, IReadOnlyList<ThemePackDto> offered)
    {
        var path = PacksPath(id);
        var text = JsonSerializer.Serialize(offered, ThemePackStore.Write);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;

        Replace(path, text);
    }

    private static ThemeSourceDto ToDto(Stored s) =>
        new(s.Id, ThemeSourceRules.Url(s.Owner, s.Repo), $"{s.Owner}/{s.Repo}", s.ThemeCount, s.LastRunUtc, s.LastSucceeded, s.LastMessage);

    private List<Stored> Load()
    {
        var path = IndexPath;
        if (!File.Exists(path)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(path, Encoding.UTF8), ThemePackStore.Write) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or edited by hand: keep the file as .bad (it may be repaired by hand) and start with no packs, so the page and the worker work again.
            log.LogWarning("The theme pack index {File} cannot be read and is set aside as .bad: {Problem}", path, ex.Message);
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { log.LogWarning("The theme pack index could not be set aside: {Problem}", move.Message); }
            return [];
        }
    }

    private void Save(List<Stored> all) => Replace(IndexPath, JsonSerializer.Serialize(all, ThemePackStore.Write));

    /// <summary>Written beside and moved over, so a request that reads at the same time never sees half a file.</summary>
    private static void Replace(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
