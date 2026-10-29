using System.Globalization;

namespace Subtune;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Starting Subtune again (e.g. the desktop icon) pokes the running copy instead.
        using var poke = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Subtune.Poke", out bool firstInstance);
        if (!firstInstance)
        {
            poke.Set();
            return;
        }

        Log.Start();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(ParseDemo(args), poke));
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
