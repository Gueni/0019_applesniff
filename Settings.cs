using System.IO;
using System.Text.Json;

namespace AppleSniff;

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

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppleSniff");
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
