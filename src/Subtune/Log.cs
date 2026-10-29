namespace Subtune;

static class Log
{
    const long MaxSize = 512 * 1024;
    static readonly Lock Gate = new();

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Paths.LocalData);
            var file = new FileInfo(Paths.Log);
            if (file.Exists && file.Length > MaxSize)
                file.Delete();
        }
        catch (IOException) { }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Paths.Log, line); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
