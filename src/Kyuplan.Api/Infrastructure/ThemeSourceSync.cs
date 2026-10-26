using System.Collections.Concurrent;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Infrastructure;

/// <summary>
/// Reads the repository of a GitHub theme pack and makes its themes the pack's offer (<see cref="ThemeSourceStore.Record"/>). A failure is not
/// thrown: it is the pack's last result, and the themes it offered so far stay. One read at a time for each pack, so a slow pack never makes
/// an admin wait for another one.
/// </summary>
public sealed class ThemeSourceSync(ThemeSourceStore store, IThemeSourceFetcher fetcher, ILogger<ThemeSourceSync> log)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Reads the pack again. <c>null</c> when there is no such pack.</summary>
    public async Task<ThemeSourceDto?> SyncAsync(string id, CancellationToken cancellationToken)
    {
        var one = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await one.WaitAsync(cancellationToken);
        try
        {
            if (store.Find(id) is not { } source || ThemeSourceRules.Parse(source.Url) is not var (owner, repo)) return null;

            try
            {
                var fetched = await fetcher.FetchAsync(owner, repo, cancellationToken);
                var (offered, problems) = Build(source, fetched);

                if (fetched.Files.Count == 0 && problems.Count == 0)
                    Fail(source, $"No themes found. The repository needs a folder '{ThemeSourceRules.Folder}' with one .json file for each theme.");
                else if (offered.Count == 0)
                    Fail(source, Summary(problems));
                else
                {
                    store.Record(id, offered, true, problems.Count > 0 ? Summary(problems) : null);
                    log.LogInformation("The theme pack {Pack} offers {Count} themes.", source.Name, offered.Count);
                }
            }
            catch (ThemeSourceException ex)
            {
                Fail(source, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reading the theme pack {Pack} failed.", source.Name);
                store.Record(id, null, false, $"Unexpected error: {ex.Message}");
            }

            return store.Find(id);
        }
        finally
        {
            one.Release();
        }
    }

    private void Fail(ThemeSourceDto source, string message)
    {
        log.LogWarning("The theme pack {Pack} could not be read: {Problem}", source.Name, message);
        store.Record(source.Id, null, false, message);
    }

    private static string Summary(List<string> problems) =>
        $"{problems.Count} skipped: {string.Join("; ", problems.Take(5))}{(problems.Count > 5 ? $"; … ({problems.Count - 5} more)" : "")}";

    /// <summary>
    /// The themes of the files, with ids that start with the pack's key. A file that is no valid theme is skipped with the reason. The name of a theme
    /// (and its id) must be unique in the pack: of two themes that share one, the file that comes first wins.
    /// </summary>
    public static (List<ThemePackDto> Offered, List<string> Problems) Build(ThemeSourceDto source, ThemeSourceFetch fetched)
    {
        var offered = new List<ThemePackDto>();
        var files = new Dictionary<string, string>(); // id -> file
        var problems = new List<string>(fetched.Problems);
        var maxIdLength = ThemeRules.MaxLength - source.Id.Length - 1;

        foreach (var file in fetched.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var label = file.Path[(ThemeSourceRules.Folder.Length + 1)..];
            try
            {
                var raw = ThemePackStore.Deserialize(file.Text);
                if (raw?.Colors is null) throw new InvalidInputException("colors is missing.");
                if (raw.Id?.Length > maxIdLength) throw new InvalidInputException($"id is longer than {maxIdLength} characters.");

                var pack = raw with { Id = $"{source.Id}-{raw.Id}", BuiltIn = false, Source = source.Name };
                if (ThemePackRules.Check(pack) is { } problem) throw new InvalidInputException(problem);
                if (files.TryGetValue(pack.Id, out var sameId)) throw new InvalidInputException($"the id '{raw.Id}' is used by {sameId} already.");
                if (offered.FirstOrDefault(p => SameName(p.Name, pack.Name) || SameName(GermanName(p), GermanName(pack))) is { } sameName)
                    throw new InvalidInputException($"the name '{pack.Name}' is used by {files[sameName.Id]} already (the names of the themes of a theme pack must be unique).");

                offered.Add(pack);
                files[pack.Id] = label;
            }
            catch (InvalidInputException ex)
            {
                problems.Add($"{label}: {ex.Message}");
            }
        }

        return (offered, problems);
    }

    /// <summary>The name a German client lists the theme under (no German name = the name): two themes must differ in both languages.</summary>
    private static string GermanName(ThemePackDto pack) => string.IsNullOrWhiteSpace(pack.NameDe) ? pack.Name : pack.NameDe;

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads the GitHub theme packs again when their time has come (<see cref="ThemeSourceStore.Due"/>); looks every minute.</summary>
public sealed class ThemeSourceWorker(ThemeSourceStore store, ThemeSourceSync sync, TimeProvider time, ILogger<ThemeSourceWorker> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Tick, time);
            do
            {
                try
                {
                    foreach (var source in store.Due()) await sync.SyncAsync(source.Id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Looking after the theme packs failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }
}
