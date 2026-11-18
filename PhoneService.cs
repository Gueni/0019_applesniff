using System.IO;
using MediaDevices;

namespace Sniffer;

/// <summary>One photo or video on the phone. <paramref name="Folder"/> is its path inside the storage, e.g. "DCIM/Camera".</summary>
public sealed record MediaItem(Func<Stream> Open, string Name, string Storage, string Folder, long Size, DateTime? Date, bool IsVideo)
{
    /// <summary>Identity that stays the same between sessions, used to remember what was already copied.</summary>
    public string Key => $"{Storage}/{Folder}/{Name}|{Size}|{Date?.Ticks ?? 0}";

    public string PhonePath => $"{Folder}/{Name}";
}

/// <summary>A connected phone (or camera) as seen before opening it.</summary>
public sealed record PhoneInfo(string Id, string Name, bool IsApple);

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
        { ".mov", ".mp4", ".m4v", ".avi", ".3gp", ".3g2", ".mts", ".m2ts", ".mkv", ".webm", ".hevc" };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".heic", ".heif", ".hif", ".png", ".gif", ".webp", ".avif", ".dng", ".raw", ".tif", ".tiff", ".bmp" };

    // On Android the photos live in these top-level folders; everything else (apps, music, LOST.DIR...) is skipped.
    private static readonly HashSet<string> AndroidMediaRoots = new(StringComparer.OrdinalIgnoreCase)
        { "DCIM", "Pictures", "Movies" };

    private static bool IsMedia(string ext) => PhotoExtensions.Contains(ext) || VideoExtensions.Contains(ext);

    private const string IndexFileName = ".sniffer-index";
    private const string PartSuffix = ".sniffer-part";

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

    private static string DisplayName(MediaDevice d)
    {
        var name = Safe(() => d.FriendlyName);
        if (name.Length == 0) name = Safe(() => d.Description);
        return name.Length == 0 ? "Phone" : name;
    }

    /// <summary>
    /// Phones and cameras plugged in over USB (MTP/PTP). USB sticks and card readers also show up as
    /// portable devices, but through a software bus rather than a USB path, so they are left out.
    /// </summary>
    public static List<PhoneInfo> ListPhones()
    {
        var phones = new List<PhoneInfo>();
        foreach (var d in AllDevices())
        {
            try
            {
                var id = Safe(() => d.DeviceId);
                if (id.StartsWith(@"\\?\usb#", StringComparison.OrdinalIgnoreCase))
                    phones.Add(new PhoneInfo(id, DisplayName(d), IsApple(d)));
            }
            finally
            {
                d.Dispose();
            }
        }
        return phones;
    }

    /// <summary>Opens the device with this id. Returns null when it is gone or refuses the connection.</summary>
    public static MediaDevice? Open(string deviceId)
    {
        MediaDevice? found = null;
        foreach (var d in AllDevices())
        {
            if (found == null && string.Equals(Safe(() => d.DeviceId), deviceId, StringComparison.OrdinalIgnoreCase)) found = d;
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
    /// Whether the photos can be read yet. A locked Android phone, or one in "charging only" mode, shows
    /// no storage at all. A locked or not-yet-allowed iPhone may show a storage whose DCIM folder is
    /// missing or empty.
    /// </summary>
    public static bool HasMedia(MediaDevice device, bool isApple)
    {
        try
        {
            foreach (var storage in device.GetRootDirectory().EnumerateDirectories())
            {
                if (!isApple)
                {
                    if (storage.EnumerateFileSystemInfos().Any()) return true;
                    continue;
                }
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

    /// <summary>Lists every photo and video on all of the phone's storages (internal memory, SD card...).</summary>
    public static List<MediaItem> Scan(MediaDevice device, bool isApple, Action<int> onCount, CancellationToken ct)
    {
        var items = new List<MediaItem>();

        foreach (var storage in device.GetRootDirectory().EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var storageName = storage.Name ?? "";
            var pending = new Stack<(MediaDirectoryInfo Dir, string Path)>();

            foreach (var top in storage.EnumerateDirectories())
            {
                var name = top.Name ?? "";
                // An iPhone only exposes its camera roll; on other phones keep to the media folders.
                if (isApple || AndroidMediaRoots.Contains(name)) pending.Push((top, name));
            }

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, path) = pending.Pop();

                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.Name;
                    // Hidden folders and files (.thumbnails, .trashed-..., .nomedia) are app caches, not photos.
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                    if (entry is MediaDirectoryInfo sub)
                    {
                        pending.Push((sub, $"{path}/{name}"));
                    }
                    else if (entry is MediaFileInfo file)
                    {
                        var ext = Path.GetExtension(name);
                        if (!IsMedia(ext)) continue;

                        // Capture date first; else the date most cameras and apps put in the name
                        // (20260927_123658.jpg, IMG-20260912-WA0004.jpeg); the file dates on Android change
                        // whenever a file is moved, so they come last.
                        var fileDate = Plausible(file.CreationTime) ?? Plausible(file.LastWriteTime);
                        var nameDate = DateFromName(name);
                        // A name with only a day (IMG-20260912-WA0004) borrows the time from the file when they agree on the day.
                        if (nameDate is { TimeOfDay.Ticks: 0 } && fileDate?.Date == nameDate) nameDate = fileDate;
                        DateTime? date = Plausible(file.DateAuthored) ?? nameDate ?? fileDate;

                        items.Add(new MediaItem(file.OpenRead, name, storageName, path, (long)file.Length, date, VideoExtensions.Contains(ext)));
                        if (items.Count % 25 == 0) onCount(items.Count);
                    }
                }
            }
        }

        onCount(items.Count);
        return items;
    }

    private static DateTime? Plausible(DateTime? d) => d is { Year: >= 1990 } && d.Value <= DateTime.Now.AddDays(2) ? d : null;

    private static readonly System.Text.RegularExpressions.Regex NameDate = new(
        // Must start the name, follow a separator, or follow a camera prefix (IMG20230101...), so that
        // digits inside hashes like file_00000000a9b0820251231 are not mistaken for dates.
        @"(?<=^|[^0-9A-Za-z]|IMG|VID|PXL|MVIMG)((?:19|20)\d{2})[-_.]?(\d{2})[-_.]?(\d{2})(?:[-_ T.]?(\d{2})[-_.]?(\d{2})[-_.]?(\d{2}))?(?!\d{3})",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reads dates like 20260927_123658, 2026-09-27 12.36.58 or IMG-20260912-WA0004 from a file name.</summary>
    public static DateTime? DateFromName(string name)
    {
        var m = NameDate.Match(name);
        if (!m.Success) return null;
        int y = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value), d = int.Parse(m.Groups[3].Value);
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        var date = new DateTime(y, mo, d);
        if (m.Groups[4].Success)
        {
            int h = int.Parse(m.Groups[4].Value), mi = int.Parse(m.Groups[5].Value), s = int.Parse(m.Groups[6].Value);
            if (h < 24 && mi < 60 && s < 60) date = date.Add(new TimeSpan(h, mi, s));
        }
        return Plausible(date);
    }

    /// <summary>
    /// Stand-in for a phone, used to try the app without one: set the SNIFFER_DEMO environment
    /// variable to a folder and its files are treated as the phone's library.
    /// </summary>
    public static List<MediaItem> ScanFolder(string folder)
    {
        var items = new List<MediaItem>();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!IsMedia(f.Extension)) continue;
            var path = f.FullName;
            var rel = Path.GetRelativePath(folder, f.DirectoryName!).Replace('\\', '/');
            items.Add(new MediaItem(() => File.OpenRead(path), f.Name, "Demo", rel, f.Length, f.LastWriteTime,
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
        FolderLayout.PhoneFolders => Path.Combine(item.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(CleanName).ToArray()),
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
                lock (progress.Errors) progress.Errors.Add($"{item.PhonePath}: {error.Message}");

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
