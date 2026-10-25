using System.Diagnostics;
using System.IO;

namespace DevShell.Services;

/// <summary>One indexed file system entry. Fields, not properties: there can be millions.</summary>
internal sealed class FileEntry
{
    public FileEntry(string name, string nameLower, string path, long size, long modifiedTicks, bool isDirectory)
    {
        Name = name;
        NameLower = nameLower;
        Path = path;
        Size = size;
        ModifiedTicks = modifiedTicks;
        IsDirectory = isDirectory;
    }

    public string Name { get; }

    public string NameLower { get; }

    public string Path { get; }

    public long Size { get; }

    public long ModifiedTicks { get; }

    public bool IsDirectory { get; }

    public string DirectoryName => System.IO.Path.GetDirectoryName(Path) ?? Path;

    public string Extension => IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name);
}

internal sealed record IndexProgress(int Entries, string CurrentFolder, int DriveIndex, int DriveCount, bool Done);

/// <summary>
/// Walks every fixed and removable drive once, keeps the result in memory for instant
/// name search and mirrors it to a cache file so the next start is usable immediately.
/// </summary>
internal sealed class FileIndexer
{
    private const string CacheMagic = "RBIDX002";

    private volatile FileEntry[] _snapshot = Array.Empty<FileEntry>();
    private volatile bool _isIndexing;
    private readonly object _cacheGate = new();

    public FileEntry[] Snapshot => _snapshot;

    public bool IsIndexing => _isIndexing;

    public int Count => _snapshot.Length;

    public event Action<IndexProgress>? Progress;

    public static string CachePath => Path.Combine(App.DataDirectory, "fileindex.bin");

    /// <summary>Loads the cached index if it matches the drives currently attached.</summary>
    public bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return false;
            }

            using var stream = File.OpenRead(CachePath);
            using var reader = new BinaryReader(stream);

            if (reader.ReadString() != CacheMagic)
            {
                return false;
            }

            var driveSignature = reader.ReadString();
            if (driveSignature != DriveSignature())
            {
                return false;
            }

            var total = reader.ReadInt32();
            if (total <= 0)
            {
                return false;
            }

            var entries = new List<FileEntry>(total);
            for (var i = 0; i < total; i++)
            {
                var isDirectory = reader.ReadBoolean();
                var size = reader.ReadInt64();
                var modified = reader.ReadInt64();
                var path = reader.ReadString();
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                entries.Add(new FileEntry(name, name.ToLowerInvariant(), path, size, modified, isDirectory));
            }

            _snapshot = entries.ToArray();
            return _snapshot.Length > 0;
        }
        catch (Exception ex)
        {
            App.Log("FileIndexer.TryLoadCache", ex);
            return false;
        }
    }

    public Task RebuildAsync(Config config, CancellationToken token) => Task.Run(() =>
    {
        if (_isIndexing)
        {
            return;
        }

        _isIndexing = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var drives = EnumerateDrives();
            var entries = new List<FileEntry>(Math.Min(200_000, config.MaxIndexEntries));
            var skipped = new HashSet<string>(config.SkippedFolderNames, StringComparer.OrdinalIgnoreCase);
            var skippedFiles = new HashSet<string>(config.SkippedFileNames, StringComparer.OrdinalIgnoreCase);
            var lastReport = 0L;
            var truncated = false;

            for (var driveIndex = 0; driveIndex < drives.Count && !truncated; driveIndex++)
            {
                var root = drives[driveIndex];
                var stack = new Stack<string>();
                stack.Push(root);

                while (stack.Count > 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    var folder = stack.Pop();
                    IEnumerable<FileSystemInfo> children;
                    try
                    {
                        children = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = false,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        });
                    }
                    catch
                    {
                        continue;
                    }

                    using var enumerator = children.GetEnumerator();
                    while (true)
                    {
                        FileSystemInfo child;
                        try
                        {
                            if (!enumerator.MoveNext())
                            {
                                break;
                            }

                            child = enumerator.Current;
                        }
                        catch
                        {
                            break;
                        }

                        var isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                        if (isDirectory)
                        {
                            if (skipped.Contains(child.Name))
                            {
                                continue;
                            }

                            stack.Push(child.FullName);
                            continue;
                        }

                        if (skippedFiles.Contains(child.Name))
                        {
                            continue;
                        }

                        long size = 0;
                        long modified = 0;
                        try
                        {
                            size = child is FileInfo file ? file.Length : 0;
                            modified = child.LastWriteTimeUtc.Ticks;
                        }
                        catch
                        {
                            // unreadable metadata: index it anyway with what we have
                        }

                        entries.Add(new FileEntry(
                            child.Name, child.Name.ToLowerInvariant(), child.FullName, size, modified, isDirectory: false));

                        if (entries.Count >= config.MaxIndexEntries)
                        {
                            truncated = true;
                            break;
                        }

                        var now = stopwatch.ElapsedMilliseconds;
                        if (now - lastReport > 300)
                        {
                            lastReport = now;
                            _snapshot = entries.ToArray();
                            Progress?.Invoke(new IndexProgress(entries.Count, folder, driveIndex + 1, drives.Count, Done: false));
                        }
                    }
                }
            }

            entries.TrimExcess();
            _snapshot = entries.ToArray();
            Progress?.Invoke(new IndexProgress(entries.Count, string.Empty, drives.Count, drives.Count, Done: true));
            App.LogInfo($"Indexed {entries.Count} files from {drives.Count} drive(s) in {stopwatch.Elapsed.TotalSeconds:0.0}s" +
                    (truncated ? $" (capped at {config.MaxIndexEntries})" : string.Empty));

            SaveCache(DriveSignatureFor(drives));
        }
        catch (Exception ex)
        {
            App.Log("FileIndexer.Rebuild", ex);
        }
        finally
        {
            _isIndexing = false;
        }
    }, token);

    private void SaveCache(string signature)
    {
        lock (_cacheGate)
        {
            try
            {
                var entries = _snapshot;
                var temp = CachePath + ".tmp";
                using (var stream = File.Create(temp))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(CacheMagic);
                    writer.Write(signature);
                    writer.Write(entries.Length);
                    foreach (var entry in entries)
                    {
                        writer.Write(entry.IsDirectory);
                        writer.Write(entry.Size);
                        writer.Write(entry.ModifiedTicks);
                        writer.Write(entry.Path);
                    }
                }

                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                App.Log("FileIndexer.SaveCache", ex);
            }
        }
    }

    public static List<string> EnumerateDrives()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                if (drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch
            {
                // drive disappeared mid-enumeration
            }
        }

        return roots;
    }

    private static string DriveSignature() => DriveSignatureFor(EnumerateDrives());

    private static string DriveSignatureFor(IEnumerable<string> roots)
        => string.Join('|', roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
}
