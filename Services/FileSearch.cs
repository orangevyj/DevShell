using System.IO;

namespace DevShell.Services;

/// <summary>One ranked search hit.</summary>
internal sealed record SearchHit(FileEntry Entry, int Score)
{
    public string Name => Entry.Name;

    public string Folder => Entry.DirectoryName;

    public string Kind => Entry.IsDirectory ? "Folder" : Entry.Extension.TrimStart('.').ToUpperInvariant();

    public string SizeText => Entry.IsDirectory ? string.Empty : FormatSize(Entry.Size);

    public string ModifiedText => Entry.ModifiedTicks <= 0
        ? string.Empty
        : new DateTime(Entry.ModifiedTicks).ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Ranks the in-memory index against a query. Two passes: a strict one that fills the
/// result list with high-confidence matches, then a relaxed fuzzy pass only if there was
/// room left, which keeps keystrokes fast even on a multi-million entry index.
/// </summary>
internal sealed class FileSearch
{
    private const int StrictThreshold = 500;
    private const int RelaxedThreshold = 150;
    private const int PerPartitionCap = 1200;
    private const int ChunkSize = 24_000;

    private readonly FileIndexer _indexer;

    public FileSearch(FileIndexer indexer) => _indexer = indexer;

    public IReadOnlyList<SearchHit> Search(string query, int max, CancellationToken token)
    {
        var indexed = _indexer.Snapshot;
        if (indexed.Length == 0 || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchHit>();
        }

        var needle = query.Trim().ToLowerInvariant();
        var tokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return Array.Empty<SearchHit>();
        }

        var strict = Collect(indexed, needle, tokens, StrictThreshold, max, token);
        if (strict.Count >= max || token.IsCancellationRequested)
        {
            return Rank(strict, max);
        }

        // Short queries match almost everything by substring, so fuzzy matching
        // is only worth it once the strict pass came up short.
        var relaxed = Collect(indexed, needle, tokens, RelaxedThreshold, max - strict.Count, token);
        strict.AddRange(relaxed.Select(r => (r.Score - 1, r.Entry)));
        return Rank(strict, max);
    }

    private static List<(int Score, FileEntry Entry)> Collect(
        FileEntry[] indexed, string needle, string[] tokens, int threshold, int wanted, CancellationToken token)
    {
        var results = new List<(int, FileEntry)>(Math.Min(wanted * 8, 4096));
        var gate = new object();
        var chunks = (indexed.Length + ChunkSize - 1) / ChunkSize;

        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = token }, chunk =>
            {
                var start = chunk * ChunkSize;
                var end = Math.Min(start + ChunkSize, indexed.Length);
                var local = new List<(int, FileEntry)>(64);

                for (var i = start; i < end; i++)
                {
                    var score = Score(indexed[i], needle, tokens, threshold);
                    if (score >= threshold)
                    {
                        local.Add((score, indexed[i]));
                        if (local.Count >= PerPartitionCap)
                        {
                            break;
                        }
                    }
                }

                if (local.Count == 0)
                {
                    return;
                }

                lock (gate)
                {
                    results.AddRange(local);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // keep whatever was collected so far
        }

        return results;
    }

    private static List<SearchHit> Rank(List<(int Score, FileEntry Entry)> candidates, int max)
    {
        candidates.Sort((a, b) =>
        {
            var byScore = b.Item1.CompareTo(a.Item1);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(a.Item2.NameLower, b.Item2.NameLower);
        });

        var hits = new List<SearchHit>(Math.Min(max, candidates.Count));
        for (var i = 0; i < candidates.Count && hits.Count < max; i++)
        {
            hits.Add(new SearchHit(candidates[i].Item2, candidates[i].Item1));
        }

        return hits;
    }

    private static int Score(FileEntry entry, string needle, string[] tokens, int threshold)
    {
        var name = entry.NameLower;
        int score;

        var index = name.IndexOf(needle, StringComparison.Ordinal);
        if (index == 0)
        {
            score = name.Length == needle.Length ? 1000 : 820;
        }
        else if (index > 0)
        {
            score = 600 - Math.Min(index, 120);
        }
        else if (tokens.Length > 1 && Array.TrueForAll(tokens, t => name.Contains(t, StringComparison.Ordinal)))
        {
            score = 520;
        }
        else if (AcronymMatches(name, tokens))
        {
            score = 470;
        }
        else if (threshold <= RelaxedThreshold)
        {
            if (IsSubsequence(name, needle))
            {
                score = 340;
            }
            else if (entry.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                score = 220;
            }
            else if (IsSubsequence(entry.Path.ToLowerInvariant(), needle))
            {
                score = 160;
            }
            else
            {
                return -1;
            }
        }
        else
        {
            return -1;
        }

        if (tokens.Length > 1)
        {
            // Every token has to appear somewhere, otherwise "code visual" style
            // queries drift into unrelated files.
            foreach (var token in tokens)
            {
                if (!name.Contains(token, StringComparison.Ordinal) &&
                    !entry.Path.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return -1;
                }
            }

            score += 40;
        }

        return score + Bonuses(entry);
    }

    private static int Bonuses(FileEntry entry)
    {
        var score = 0;

        if (!entry.IsDirectory)
        {
            score += entry.Extension.ToLowerInvariant() switch
            {
                ".exe" or ".com" => 45,
                ".lnk" => 40,
                ".msi" => 25,
                ".bat" or ".cmd" or ".ps1" => 15,
                ".pdf" or ".txt" or ".docx" or ".xlsx" or ".md" => 10,
                _ => 0,
            };
        }

        // shallow files beat deep ones
        var depth = 0;
        foreach (var c in entry.Path)
        {
            if (c == '\\')
            {
                depth++;
            }
        }

        score -= Math.Min(depth, 12) * 6;
        score -= Math.Min(entry.Name.Length / 4, 20);

        if (entry.ModifiedTicks > 0)
        {
            var age = DateTime.UtcNow - new DateTime(entry.ModifiedTicks);
            if (age.TotalDays < 30)
            {
                score += 25;
            }
            else if (age.TotalDays < 365)
            {
                score += 10;
            }
        }

        return score;
    }

    private static bool AcronymMatches(string name, string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return false;
        }

        // initials of the words in the file name, e.g. "visual studio code" -> "vsc"
        Span<char> initials = stackalloc char[32];
        var count = 0;
        var atWordStart = true;

        foreach (var c in name)
        {
            var isSeparator = !char.IsLetterOrDigit(c);
            if (isSeparator)
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart && count < initials.Length)
            {
                initials[count++] = c;
                atWordStart = false;
            }
        }

        if (count == 0)
        {
            return false;
        }

        var target = tokens[0];
        if (target.Length > count)
        {
            return false;
        }

        var position = 0;
        for (var i = 0; i < count && position < target.Length; i++)
        {
            if (initials[i] == target[position])
            {
                position++;
            }
        }

        return position == target.Length;
    }

    private static bool IsSubsequence(string haystack, string needle)
    {
        var position = 0;
        foreach (var c in haystack)
        {
            if (position < needle.Length && c == needle[position])
            {
                position++;
                if (position == needle.Length)
                {
                    return true;
                }
            }
        }

        return position == needle.Length;
    }
}
