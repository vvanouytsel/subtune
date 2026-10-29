using System.Text.Json;

namespace Subtune;

sealed class Settings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Overlay bounds in physical pixels; null until the user has placed it.
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public bool LyricsShown { get; set; } = true;
    public bool ShowNextLine { get; set; } = true;
    public int OffsetMs { get; set; }
    public string FontFamily { get; set; } = "Segoe UI Semibold";

    /// <summary>Follow any media app when Spotify isn't running.</summary>
    public bool AnyPlayer { get; set; }

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"couldn't save settings: {e.Message}");
        }
    }
}
