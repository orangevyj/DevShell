using System.IO;

namespace DevShell.Services;

/// <summary>
/// Minimal INI reader for the two files Windows uses for shell metadata: desktop.ini and
/// .url internet shortcuts. Both are plain INI, both are read-only as far as we are concerned,
/// and both may sit on disk with the hidden+system attributes set - File.ReadAllLines does not care.
/// </summary>
internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    private IniFile()
    {
    }

    public static IniFile? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var ini = new IniFile();
            var section = string.Empty;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is ';' or '#')
                {
                    continue;
                }

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1].Trim();
                    ini._sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                if (!ini._sections.TryGetValue(section, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ini._sections[section] = values;
                }

                values[line[..split].Trim()] = line[(split + 1)..].Trim();
            }

            return ini;
        }
        catch (Exception ex)
        {
            App.Log($"IniFile.Read({path})", ex);
            return null;
        }
    }

    public string? Get(string section, string key)
        => _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

    public bool HasSection(string section) => _sections.ContainsKey(section);
}
