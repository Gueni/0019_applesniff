using System.IO;
using MediaDevices;

namespace AppleSniff;

/// <summary>One photo or video on the phone.</summary>
public sealed record MediaItem(Func<Stream> Open, string Name, string PhoneFolder, long Size, DateTime? Date, bool IsVideo)
{
    /// <summary>Identity that stays the same between sessions, used to remember what was already copied.</summary>
    public string Key => $"{PhoneFolder}/{Name}|{Size}|{Date?.Ticks ?? 0}";
}

public sealed record CopyJob(MediaItem Item, string TargetPath);

public sealed class CopyPlan
{
    public List<CopyJob> Jobs { get; } = new();
    public long Bytes { get; set; }
    public int AlreadyCopied { get; set; }
    public long FreeBytes { get; set; } = long.MaxValue;
    public bool EnoughSpace => FreeBytes >= Bytes;
}

/// <summary>Live counters written by the copy thread and read by the UI timer.</summary>
public sealed class CopyProgress
{
    public long BytesDone;
    public int FilesDone;
    public int FilesFailed;
    public volatile string CurrentName = "";
    public volatile bool DeviceLost;
    public List<string> Errors { get; } = new();
}

public static class PhoneService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".avi", ".3gp", ".mts" };

    // Edit sidecars and other bookkeeping files that are useless outside the Photos app.
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".aae", ".plist", ".xmp", ".db", ".ini" };

    private const string IndexFileName = ".applesniff-index";
    private const string PartSuffix = ".applesniff-part";

    // ---------------------------------------------------------------- devices

    private static IEnumerable<MediaDevice> AllDevices() => MediaDeviceManager.Instance.GetDevices() ?? [];

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? ""; } catch { return ""; }
    }

    private static bool IsApple(MediaDevice d)
    {
        var text = string.Join(' ', Safe(() => d.FriendlyName), Safe(() => d.Manufacturer), Safe(() => d.Description));
        return text.Contains("Apple", StringComparison.OrdinalIgnoreCase)
            || text.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("iPad", StringComparison.OrdinalIgnoreCase);
    }

    public static string DisplayName(MediaDevice d)
    {
        var name = Safe(() => d.FriendlyName);
        if (name.Length == 0) name = Safe(() => d.Description);
        return name.Length == 0 ? "iPhone" : name;
    }

    /// <summary>Finds the first connected Apple device and opens it. Returns null when none is plugged in.</summary>
    public static MediaDevice? OpenPhone()
    {
        MediaDevice? found = null;
        foreach (var d in AllDevices())
        {
            if (found == null && IsApple(d)) found = d;
            else d.Dispose();
        }
        if (found == null) return null;

        try
        {
            found.Connect(MediaDeviceAccess.GenericRead);
            return found;
        }
        catch (Exception ex)
        {
            Log.Error("Connect", ex);
            found.Dispose();
            return null;
        }
    }

    public static bool IsPresent(string deviceId)
    {
        var present = false;
        try
        {
            foreach (var d in AllDevices())
            {
                if (string.Equals(Safe(() => d.DeviceId), deviceId, StringComparison.OrdinalIgnoreCase)) present = true;
                d.Dispose();
            }
        }
        catch
        {
            return false;
        }
        return present;
    }

    /// <summary>
    /// A locked or not-yet-allowed iPhone shows up with no storage, or with a storage whose DCIM
    /// folder is missing or empty. Only once photo folders appear is the library really readable.
    /// </summary>
    public static bool HasMedia(MediaDevice device)
    {
        try
        {
            foreach (var storage in device.GetRootDirectory().EnumerateDirectories())
            {
                var dcim = storage.EnumerateDirectories().FirstOrDefault(d => string.Equals(d.Name, "DCIM", StringComparison.OrdinalIgnoreCase));
                if (dcim != null && dcim.EnumerateFileSystemInfos().Any()) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("HasMedia", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------- scan

    public static List<MediaItem> Scan(MediaDevice device, Action<int> onCount, CancellationToken ct)
    {
        var items = new List<MediaItem>();
        var pending = new Stack<MediaDirectoryInfo>();
        pending.Push(device.GetRootDirectory());

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var folderName = dir.Name ?? "";

            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry is MediaDirectoryInfo sub)
                {
                    pending.Push(sub);
                }
                else if (entry is MediaFileInfo file)
                {
                    var name = file.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var ext = Path.GetExtension(name);
                    if (IgnoredExtensions.Contains(ext)) continue;

                    DateTime? date = file.DateAuthored ?? file.CreationTime ?? file.LastWriteTime;
                    if (date is { Year: < 1990 }) date = null;

                    items.Add(new MediaItem(file.OpenRead, name, folderName, (long)file.Length, date, VideoExtensions.Contains(ext)));
                    if (items.Count % 25 == 0) onCount(items.Count);
                }
            }
        }

        onCount(items.Count);
        return items;
    }

    /// <summary>
    /// Stand-in for a phone, used to try the app without one: set the APPLESNIFF_DEMO environment
    /// variable to a folder and its files are treated as the phone's library.
    /// </summary>
    public static List<MediaItem> ScanFolder(string folder)
    {
        var items = new List<MediaItem>();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (IgnoredExtensions.Contains(f.Extension)) continue;
            var path = f.FullName;
            items.Add(new MediaItem(() => File.OpenRead(path), f.Name, f.Directory?.Name ?? "", f.Length, f.LastWriteTime,
                VideoExtensions.Contains(f.Extension)));
        }
        return items;
    }

    // ------------------------------------------------------------------- plan

    private static string CleanName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }

    private static string RelativeDir(MediaItem item, FolderLayout layout) => layout switch
    {
        FolderLayout.ByMonth => item.Date is { } d ? Path.Combine(d.ToString("yyyy"), d.ToString("yyyy-MM")) : "Unknown date",
        FolderLayout.PhoneFolders => CleanName(item.PhoneFolder),
        _ => "",
    };

    private static Dictionary<string, string> LoadIndex(string destination)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(destination, IndexFileName);
        try
        {
            if (!File.Exists(path)) return index;
            foreach (var line in File.ReadLines(path))
            {
                var tab = line.LastIndexOf('\t');
                if (tab > 0) index[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch
        {
            // Without the index we still recognise copies by name and size.
        }
        return index;
    }

    /// <summary>Decides, for every item, whether it is already in the destination and otherwise where it goes.</summary>
    public static CopyPlan BuildPlan(IReadOnlyList<MediaItem> items, string destination, FolderLayout layout, CancellationToken ct)
    {
        var plan = new CopyPlan();
        var index = LoadIndex(destination);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One directory listing per folder is far faster than a File.Exists per photo.
        var listings = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, long> Listing(string dir)
        {
            if (listings.TryGetValue(dir, out var known)) return known;
            var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(dir))
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                        files[f.Name] = f.Length;
            }
            catch
            {
                // Unreadable folder: treat as empty, the copy itself will report the real problem.
            }
            listings[dir] = files;
            return files;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (index.TryGetValue(item.Key, out var rel))
            {
                var full = Path.Combine(destination, rel);
                var dirOfRel = Path.GetDirectoryName(full) ?? destination;
                if (Listing(dirOfRel).ContainsKey(Path.GetFileName(full)))
                {
                    plan.AlreadyCopied++;
                    continue;
                }
            }

            var dir = Path.Combine(destination, RelativeDir(item, layout));
            var existing = Listing(dir);
            var clean = CleanName(item.Name);
            var stem = Path.GetFileNameWithoutExtension(clean);
            var ext = Path.GetExtension(clean);

            string? target = null;
            var alreadyThere = false;
            for (var n = 1; n < 10_000; n++)
            {
                var candidate = n == 1 ? clean : $"{stem} ({n}){ext}";
                var path = Path.Combine(dir, candidate);
                if (claimed.Contains(path)) continue;
                if (existing.TryGetValue(candidate, out var length))
                {
                    if (length == item.Size) { alreadyThere = true; break; }
                    continue; // same name, different file: try the next free name
                }
                target = path;
                break;
            }

            if (alreadyThere || target == null)
            {
                plan.AlreadyCopied++;
                continue;
            }

            claimed.Add(target);
            plan.Jobs.Add(new CopyJob(item, target));
            plan.Bytes += item.Size;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\")) plan.FreeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // Unknown free space (e.g. a network share): do not block the copy.
        }

        return plan;
    }

    // ------------------------------------------------------------------- copy

    public static void Copy(CopyPlan plan, string destination, string deviceId, CopyProgress progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var indexPath = Path.Combine(destination, IndexFileName);
        var indexIsNew = !File.Exists(indexPath);
        using var index = new StreamWriter(new FileStream(indexPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        if (indexIsNew)
        {
            try { File.SetAttributes(indexPath, File.GetAttributes(indexPath) | FileAttributes.Hidden); } catch { }
        }

        var buffer = new byte[1 << 20];
        long completedBytes = 0;
        var consecutiveFailures = 0;

        foreach (var job in plan.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            var item = job.Item;
            progress.CurrentName = item.Name;

            Exception? error = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    CopyOne(job, buffer, completedBytes, progress, ct);
                    error = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                    Interlocked.Exchange(ref progress.BytesDone, completedBytes);
                    if (attempt == 1) Thread.Sleep(600);
                }
            }

            completedBytes += item.Size;
            Interlocked.Exchange(ref progress.BytesDone, completedBytes);

            if (error == null)
            {
                consecutiveFailures = 0;
                Interlocked.Increment(ref progress.FilesDone);
                index.WriteLine($"{item.Key}\t{Path.GetRelativePath(destination, job.TargetPath)}");
            }
            else
            {
                Interlocked.Increment(ref progress.FilesFailed);
                lock (progress.Errors) progress.Errors.Add($"{item.PhoneFolder}/{item.Name}: {error.Message}");

                // A run of failures usually means the cable was pulled or the phone locked itself out.
                if (++consecutiveFailures >= 3 && !IsPresent(deviceId))
                {
                    progress.DeviceLost = true;
                    return;
                }
            }
        }
    }

    private static void CopyOne(CopyJob job, byte[] buffer, long completedBytes, CopyProgress progress, CancellationToken ct)
    {
        var item = job.Item;
        var dir = Path.GetDirectoryName(job.TargetPath)!;
        Directory.CreateDirectory(dir);
        var part = job.TargetPath + PartSuffix;

        try
        {
            using (var source = item.Open())
            using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
            {
                long read = 0;
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, n);
                    read += n;
                    // The phone's reported size can be an estimate, so never let one file overshoot its share.
                    Interlocked.Exchange(ref progress.BytesDone, completedBytes + Math.Min(read, item.Size));
                }

                if (read == 0 && item.Size > 0) throw new IOException("The phone returned no data for this file.");
            }

            if (item.Date is { } date)
            {
                try
                {
                    File.SetCreationTime(part, date);
                    File.SetLastWriteTime(part, date);
                }
                catch
                {
                    // Timestamps are a nicety; the copy itself succeeded.
                }
            }

            File.Move(part, job.TargetPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }
}
