using System.Collections.Concurrent;
using System.Text;
using MatFile.Web.Storage;

namespace MatFile.Web.Services;

/// <summary>
/// Rekursive Suche über einen Speicherort - nach Namen und auf Wunsch im Dateiinhalt.
/// Läuft im Hintergrund, meldet Fortschritt und lässt sich abbrechen. Ergebnisse leben
/// nur im Speicher: eine Suche ist ein Blick, kein Datenbestand.
/// </summary>
public sealed class SearchService
{
    /// <summary>Mehr Treffer braucht niemand in einem Fenster - danach gilt die Suche als gekürzt.</summary>
    public const int MaxHits = 500;

    /// <summary>Schutz vor Ausflügen in riesige Bäume.</summary>
    public const int MaxFolders = 20_000;

    public const int MaxDepth = 24;

    /// <summary>Größere Dateien werden beim Inhaltsvergleich übersprungen.</summary>
    public const long MaxContentBytes = 8L * 1024 * 1024;

    public const int MinQueryLength = 2;

    private const int RunsPerUser = 4;
    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SearchService> _log;
    private readonly ConcurrentDictionary<string, SearchRun> _runs = new();

    public SearchService(IServiceScopeFactory scopes, ILogger<SearchService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public record SearchHit(
        string Name,
        string Path,
        string ParentPath,
        bool IsDirectory,
        long Size,
        DateTime Modified,
        string Preview,
        string? Snippet);

    public record SearchSnapshot(
        string Id,
        string State,
        string Query,
        bool SearchContent,
        bool IncludeHidden,
        long RootId,
        string RootName,
        string BasePath,
        int Percent,
        int Folders,
        int Files,
        string Current,
        int Total,
        bool Truncated,
        string? Error,
        IReadOnlyList<SearchHit> Hits);

    /// <summary>Startet eine Suche und liefert deren Kennung.</summary>
    public string Start(long userId, long rootId, string? basePath, string query, bool searchContent, bool includeHidden)
    {
        Prune(userId);

        var run = new SearchRun
        {
            Id = Guid.NewGuid().ToString("n"),
            UserId = userId,
            RootId = rootId,
            BasePath = PathGuard.Normalize(basePath),
            Query = query.Trim(),
            SearchContent = searchContent,
            IncludeHidden = includeHidden
        };

        _runs[run.Id] = run;

        // Absichtlich ohne await: die Suche läuft weiter, während die Antwort schon zurückgeht.
        _ = Task.Run(() => RunAsync(run));

        return run.Id;
    }

    public SearchSnapshot? Snapshot(string id, long userId, int from)
    {
        if (!_runs.TryGetValue(id, out var run) || run.UserId != userId)
        {
            return null;
        }

        return run.ToSnapshot(from);
    }

    public bool Cancel(string id, long userId)
    {
        if (!_runs.TryGetValue(id, out var run) || run.UserId != userId)
        {
            return false;
        }

        run.CancelQuietly();
        return true;
    }

    // ---------------------------------------------------------------- Durchlauf

    private async Task RunAsync(SearchRun run)
    {
        var token = run.Token;

        try
        {
            using var scope = _scopes.CreateScope();
            var roots = scope.ServiceProvider.GetRequiredService<StorageRootService>();

            var opened = await roots.OpenAsync(run.UserId, run.RootId);
            if (opened is not { } access)
            {
                run.Finish("failed", "root_not_found");
                return;
            }

            run.RootName = access.Root.Name;

            await WalkAsync(run, access.Provider, token);

            run.Finish(token.IsCancellationRequested ? "cancelled" : "completed", null);
        }
        catch (OperationCanceledException)
        {
            run.Finish("cancelled", null);
        }
        catch (Exception exception)
        {
            _log.LogWarning(exception, "Suche {Id} abgebrochen", run.Id);
            run.Finish("failed", exception is StorageException storage ? storage.Code : "error");
        }
    }

    private async Task WalkAsync(SearchRun run, IStorageProvider provider, CancellationToken token)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((run.BasePath, 0));

        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (path, depth) = queue.Dequeue();
            run.Enter(path, queue.Count);

            StorageListing listing;

            try
            {
                listing = await provider.ListAsync(path, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (depth > 0)
            {
                // Ein Ordner ohne Leserecht beendet keine Suche - der Startordner schon.
                continue;
            }

            foreach (var entry in listing.Entries)
            {
                token.ThrowIfCancellationRequested();

                if (entry.IsHidden && !run.IncludeHidden)
                {
                    continue;
                }

                var nameMatches = entry.Name.Contains(run.Query, StringComparison.OrdinalIgnoreCase);

                if (entry.IsDirectory)
                {
                    if (nameMatches)
                    {
                        run.Add(ToHit(entry, null));
                    }

                    // Symlinks werden nicht verfolgt, sonst dreht die Suche im Kreis.
                    if (depth < MaxDepth && !entry.IsSymlink)
                    {
                        queue.Enqueue((entry.Path, depth + 1));
                    }

                    continue;
                }

                run.CountFile();

                if (nameMatches)
                {
                    run.Add(ToHit(entry, null));
                }
                else if (run.SearchContent && IsTextCandidate(entry))
                {
                    var snippet = await FindInFileAsync(provider, entry, run.Query, token);

                    if (snippet is not null)
                    {
                        run.Add(ToHit(entry, snippet));
                    }
                }
            }

            if (run.HitCount >= MaxHits || run.Folders >= MaxFolders)
            {
                run.MarkTruncated();
                return;
            }
        }
    }

    private static SearchHit ToHit(StorageEntry entry, string? snippet) => new(
        entry.Name,
        entry.Path,
        PathGuard.GetParent(entry.Path) ?? string.Empty,
        entry.IsDirectory,
        entry.Size,
        entry.ModifiedDate,
        entry.IsDirectory ? "none" : PreviewPolicy.KindOf(entry.Name).ToString().ToLowerInvariant(),
        snippet);

    /// <summary>
    /// Bilder, Videos, Ton und PDF fallen weg; alles andere wird versucht und beim
    /// ersten Nullbyte als Binärdatei verworfen.
    /// </summary>
    private static bool IsTextCandidate(StorageEntry entry)
        => entry.Size > 0
           && entry.Size <= MaxContentBytes
           && PreviewPolicy.KindOf(entry.Name) is PreviewKind.Text or PreviewKind.None;

    /// <summary>Sucht die Zeichenfolge im Datenstrom und gibt die Fundstelle als Textzeile zurück.</summary>
    private static async Task<string?> FindInFileAsync(
        IStorageProvider provider, StorageEntry entry, string needle, CancellationToken token)
    {
        try
        {
            await using var stream = await provider.OpenReadAsync(entry.Path, token);

            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[64 * 1024];
            var chars = new char[64 * 1024 + 16];
            var overlap = Math.Max(0, needle.Length - 1);
            var tail = string.Empty;
            var readTotal = 0L;
            var firstBlock = true;

            while (true)
            {
                var count = await stream.ReadAsync(bytes, token);
                if (count == 0)
                {
                    return null;
                }

                if (firstBlock)
                {
                    firstBlock = false;

                    if (Array.IndexOf(bytes, (byte)0, 0, count) >= 0)
                    {
                        return null;
                    }
                }

                var written = decoder.GetChars(bytes, 0, count, chars, 0);
                var window = tail + new string(chars, 0, written);

                var index = window.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return Snippet(window, index, needle.Length);
                }

                readTotal += count;
                if (readTotal >= MaxContentBytes)
                {
                    return null;
                }

                tail = overlap == 0 ? string.Empty : window[^Math.Min(overlap, window.Length)..];
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Die Zeile um die Fundstelle, bei sehr langen Zeilen nur der Ausschnitt darum.</summary>
    private static string Snippet(string text, int index, int length)
    {
        var start = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, index - 1)) + 1;
        var end = text.IndexOfAny(['\n', '\r'], Math.Min(text.Length - 1, index + length));

        if (end < 0)
        {
            end = text.Length;
        }

        var line = text[start..end];
        var relative = index - start;

        if (line.Length > 200)
        {
            var from = Math.Max(0, relative - 60);
            var take = Math.Min(200, line.Length - from);
            line = (from > 0 ? "…" : string.Empty) + line.Substring(from, take) + (from + take < line.Length ? "…" : string.Empty);
        }

        return line.Trim();
    }

    // ---------------------------------------------------------------- Aufräumen

    private void Prune(long userId)
    {
        var now = DateTime.UtcNow;

        foreach (var entry in _runs)
        {
            if (entry.Value.FinishedUtc is { } finished && now - finished > KeepFinished)
            {
                _runs.TryRemove(entry.Key, out _);
            }
        }

        var own = _runs.Values
            .Where(run => run.UserId == userId)
            .OrderByDescending(run => run.StartedUtc)
            .Skip(RunsPerUser - 1)
            .ToList();

        foreach (var run in own)
        {
            run.CancelQuietly();
            _runs.TryRemove(run.Id, out _);
        }
    }

    /// <summary>Ein laufender oder abgeschlossener Suchauftrag.</summary>
    private sealed class SearchRun
    {
        private readonly object _sync = new();
        private readonly List<SearchHit> _hits = [];
        private readonly CancellationTokenSource _cancellation = new();

        public required string Id { get; init; }
        public required long UserId { get; init; }
        public required long RootId { get; init; }
        public required string BasePath { get; init; }
        public required string Query { get; init; }
        public bool SearchContent { get; init; }
        public bool IncludeHidden { get; init; }

        public string RootName { get; set; } = string.Empty;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; private set; }

        public CancellationToken Token => _cancellation.Token;

        private string _state = "running";
        private string? _error;
        private string _current = string.Empty;
        private int _folders;
        private int _files;
        private int _pending;
        private bool _truncated;

        public int Folders => Volatile.Read(ref _folders);

        public int HitCount
        {
            get
            {
                lock (_sync)
                {
                    return _hits.Count;
                }
            }
        }

        public void Enter(string path, int pending)
        {
            lock (_sync)
            {
                _current = path;
                _pending = pending;
                _folders++;
            }
        }

        public void CountFile()
        {
            lock (_sync)
            {
                _files++;
            }
        }

        public void Add(SearchHit hit)
        {
            lock (_sync)
            {
                if (_hits.Count < MaxHits)
                {
                    _hits.Add(hit);
                }
                else
                {
                    _truncated = true;
                }
            }
        }

        public void MarkTruncated()
        {
            lock (_sync)
            {
                _truncated = true;
            }
        }

        public void Finish(string state, string? error)
        {
            lock (_sync)
            {
                _state = state;
                _error = error;
                _current = string.Empty;
                FinishedUtc = DateTime.UtcNow;
            }
        }

        public void CancelQuietly()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Bereits beendet.
            }
        }

        public SearchSnapshot ToSnapshot(int from)
        {
            lock (_sync)
            {
                var start = Math.Clamp(from, 0, _hits.Count);

                // Ohne Gesamtzahl gibt es keinen echten Prozentwert - der Anteil der
                // erledigten Ordner an den bekannten Ordnern kommt dem am nächsten.
                var known = _folders + _pending;
                var percent = _state == "running"
                    ? (known == 0 ? 0 : Math.Clamp(_folders * 100 / known, 1, 99))
                    : 100;

                return new SearchSnapshot(
                    Id, _state, Query, SearchContent, IncludeHidden, RootId, RootName, BasePath,
                    percent, _folders, _files, _current, _hits.Count, _truncated, _error,
                    _hits.GetRange(start, _hits.Count - start));
            }
        }
    }
}
