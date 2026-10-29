namespace Subtune;

static class Paths
{
    /// <summary>Roaming data: settings and the user's own .lrc files.</summary>
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtune");

    /// <summary>Machine-local data: lyrics cache and log.</summary>
    public static string LocalData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subtune");

    public static string Settings => Path.Combine(AppData, "settings.json");
    public static string UserLyrics => Path.Combine(AppData, "lyrics");
    public static string Cache => Path.Combine(LocalData, "cache");
    public static string Log => Path.Combine(LocalData, "subtune.log");
}
