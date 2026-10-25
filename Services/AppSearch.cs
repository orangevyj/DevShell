using System.IO;

namespace DevShell.Services;

/// <summary>One ranked app hit: a Start-menu shortcut and how well it matches the query.</summary>
internal sealed record AppHit(StartApp App, int Score)
{
    public string Name => App.Name;

    /// <summary>The folder the shortcut lives in, which is what "Show folder" opens.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(App.ShortcutPath) ?? string.Empty;

    public string Kind => "App";

    public string Path => App.ShortcutPath;
}

/// <summary>
/// Ranks the Start menu's own app list against a query. It is a plain scan of a few hundred names,
/// so it stays on the calling thread; the file index is not involved and is not touched.
/// </summary>
internal static class AppSearch
{
    public static IReadOnlyList<AppHit> Search(IEnumerable<StartApp> apps, string query, int max)
    {
        var needle = query.Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return Array.Empty<AppHit>();
        }

        var tokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return Array.Empty<AppHit>();
        }

        var hits = new List<AppHit>(32);

        foreach (var app in apps)
        {
            var score = Score(app, needle, tokens);
            if (score > 0)
            {
                hits.Add(new AppHit(app, score));
            }
        }

        hits.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0
                ? byScore
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return hits.Count <= max ? hits : hits.GetRange(0, max);
    }

    private static int Score(StartApp app, string needle, string[] tokens)
    {
        var name = app.Name.ToLowerInvariant();
        var path = app.ShortcutPath.ToLowerInvariant();
        int score;

        var index = name.IndexOf(needle, StringComparison.Ordinal);
        if (index == 0)
        {
            score = name.Length == needle.Length ? 1000 : 860;
        }
        else if (index > 0)
        {
            score = 700 - Math.Min(index, 120);

            // A hit on the start of a word ("visual studio" for "studio") reads better than
            // one buried in the middle of one.
            if (index > 0 && !char.IsLetterOrDigit(name[index - 1]))
            {
                score += 80;
            }
        }
        else if (tokens.Length > 1 && Array.TrueForAll(tokens, token => name.Contains(token, StringComparison.Ordinal)))
        {
            score = 560;
        }
        else if (IsSubsequence(name, needle))
        {
            score = 380;
        }
        else if (path.Contains(needle, StringComparison.Ordinal))
        {
            score = 240;
        }
        else if (IsSubsequence(path, needle))
        {
            score = 200;
        }
        else
        {
            return -1;
        }

        if (tokens.Length > 1)
        {
            foreach (var token in tokens)
            {
                if (!name.Contains(token, StringComparison.Ordinal) && !path.Contains(token, StringComparison.Ordinal))
                {
                    return -1;
                }
            }

            score += 40;
        }

        // Between two similar names the shorter one is usually the app itself rather than a helper.
        return score - Math.Min(name.Length / 3, 30);
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
