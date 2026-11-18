using System.IO;
using System.Text.Json;

namespace Sniffer;

public enum FolderLayout
{
    ByMonth = 0,
    PhoneFolders = 1,
    Flat = 2,
}

public sealed class Settings
{
    public string? Destination { get; set; }
    public FolderLayout Layout { get; set; } = FolderLayout.ByMonth;
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeVideos { get; set; } = true;
    /// <summary>0 any date, 1 last 7 days, 2 last 30 days, 3 last 12 months, 4 since <see cref="Since"/>.</summary>
    public int DateFilter { get; set; }
    public DateTime? Since { get; set; }
    /// <summary>Start copying as soon as a phone is connected and read.</summary>
    public bool AutoStart { get; set; }
    public string? LastDeviceId { get; set; }

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sniffer");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // A corrupt settings file just means starting from defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember the folder is not worth interrupting the user.
        }
    }
}
