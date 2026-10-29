using System.Globalization;

namespace Subtune;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Subtune.SingleInstance", out bool firstInstance);
        if (!firstInstance)
            return;

        Log.Start();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(ParseDemo(args)));
    }

    // --demo [seconds]: fake-play a known song so the overlay can be tried without Spotify.
    static double? ParseDemo(string[] args)
    {
        int i = Array.IndexOf(args, "--demo");
        if (i < 0)
            return null;
        return i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var start)
            ? start
            : 0;
    }
}
