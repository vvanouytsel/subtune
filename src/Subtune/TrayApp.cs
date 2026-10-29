using System.Diagnostics;
using Microsoft.Win32;

namespace Subtune;

/// <summary>
/// Ties it together: follows the player, fetches lyrics, decides which line to show, and owns the
/// tray icon and hotkeys. The line timer only wakes up when the next line is due (or once a
/// second at most), and stops entirely when nothing is playing.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(150); // reading takes a moment
    static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(5);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Settings _settings = Settings.Load();
    readonly Overlay _overlay;
    readonly INowPlaying _player;
    readonly LyricsProvider _provider = new();
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _tray;
    readonly Hotkeys _hotkeys = new();
    readonly System.Windows.Forms.Timer _lineTimer = new();

    readonly ToolStripMenuItem _nowPlaying, _showLyrics, _moveResize, _showNext, _timing, _startup;

    string? _trackKey;
    Lyrics? _lyrics;
    CancellationTokenSource? _fetch;
    string? _flash;
    long _flashStarted;

    public TrayApp(double? demoStart, WaitHandle poke)
    {
        _overlay = new Overlay(_settings);
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _player = demoStart is { } start ? new DemoPlayer(start) : new MediaWatcher(_settings.AnyPlayer);

        var toggleKey = _hotkeys.Register(Keys.L, ToggleLyrics);
        var moveKey = _hotkeys.Register(Keys.M, ToggleMoveResize);
        var soonerKey = _hotkeys.Register(Keys.Right, () => Nudge(+250));
        var laterKey = _hotkeys.Register(Keys.Left, () => Nudge(-250));
        _overlay.LockHint = moveKey ?? "Use the tray menu";

        _nowPlaying = new ToolStripMenuItem("Nothing playing") { Enabled = false };
        _showLyrics = Item("Show lyrics", toggleKey, ToggleLyrics);
        _moveResize = Item("Move / resize", moveKey, ToggleMoveResize);
        _showNext = Item("Show next line", null, ToggleNextLine);
        _timing = new ToolStripMenuItem();
        _timing.DropDownItems.AddRange([
            Item("Lyrics sooner (+0.25s)", soonerKey, () => Nudge(+250)),
            Item("Lyrics later (-0.25s)", laterKey, () => Nudge(-250)),
            Item("Reset timing", null, () => Nudge(-_settings.OffsetMs)),
        ]);
        _startup = Item("Start with Windows", null, ToggleStartup);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _nowPlaying,
            new ToolStripSeparator(),
            _showLyrics, _moveResize, _showNext, _timing,
            new ToolStripSeparator(),
            Item("Open my lyrics folder", null, OpenLyricsFolder),
            _startup,
            new ToolStripSeparator(),
            Item("Quit", null, ExitThread),
        ]);
        menu.Opening += (_, _) => _startup.Checked = StartsWithWindows;

        _tray = new NotifyIcon { Icon = CreateIcon(), Text = "Subtune", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleLyrics(); };
        RefreshMenu();

        _lineTimer.Tick += (_, _) => UpdateLine();
        _player.Changed += () => _ui.Post(_ => UpdateLine(), null);
        _ = StartPlayerAsync();
        ThreadPool.RegisterWaitForSingleObject(poke, (_, _) => _ui.Post(_ => OnStartedAgain(), null), null, -1, false);

        Flash($"Subtune is running · {moveKey ?? "tray menu"} to move or resize");
        Log.Write($"started (hotkeys: {toggleKey}, {moveKey}, {soonerKey}, {laterKey})");
    }

    async Task StartPlayerAsync()
    {
        try
        {
            await _player.StartAsync();
        }
        catch (Exception e)
        {
            Log.Write($"couldn't read Windows media controls: {e}");
            Flash("Couldn't read what's playing; see the log");
        }
    }

    // ---- the core loop ----

    void UpdateLine()
    {
        var (track, position, playing) = _player.Snapshot();
        if (track?.Key != _trackKey)
            OnTrackChanged(track);

        string current = "", next = "";
        TimeSpan? untilNextLine = null;
        if (playing && _lyrics is { Status: LyricsStatus.Synced, Lines.Count: > 0 } lyrics)
        {
            var at = position + TimeSpan.FromMilliseconds(_settings.OffsetMs) + Lead;
            int i = lyrics.IndexAt(at);
            if (i >= 0)
                current = lyrics.Lines[i].Text is { Length: > 0 } text ? text : "♪";
            next = lyrics.Lines.Skip(i + 1).FirstOrDefault(l => l.Text.Length > 0)?.Text ?? "";
            if (i + 1 < lyrics.Lines.Count)
                untilNextLine = lyrics.Lines[i + 1].Time - at;
        }
        if (!_settings.ShowNextLine)
            next = "";

        TimeSpan? untilFlashEnds = null;
        if (_flash is not null)
        {
            var left = FlashDuration - Stopwatch.GetElapsedTime(_flashStarted);
            if (left > TimeSpan.Zero) { next = _flash; untilFlashEnds = left; }
            else _flash = null;
        }

        _overlay.SetLines(current, next);
        Schedule(playing && _lyrics?.Status == LyricsStatus.Synced, untilNextLine, untilFlashEnds);
    }

    void Schedule(bool following, TimeSpan? untilNextLine, TimeSpan? untilFlashEnds)
    {
        TimeSpan? wait = null;
        if (following)
            wait = untilNextLine is { } n && n < TimeSpan.FromSeconds(1) ? n : TimeSpan.FromSeconds(1);
        if (untilFlashEnds is { } f && (wait is null || f < wait))
            wait = f;

        if (wait is not { } w)
        {
            _lineTimer.Stop();
            return;
        }
        _lineTimer.Interval = Math.Clamp((int)w.TotalMilliseconds + 5, 15, 1000);
        _lineTimer.Start();
    }

    void OnTrackChanged(Track? track)
    {
        _trackKey = track?.Key;
        _lyrics = null;
        _fetch?.Cancel();
        _nowPlaying.Text = track is null ? "Nothing playing" : Truncate(track.ToString(), 60);
        _tray.Text = track is null ? "Subtune" : Truncate($"Subtune · {track}", 127);
        if (track is null)
            return;

        Log.Write($"now playing: {track}");
        var fetch = _fetch = new CancellationTokenSource();
        var key = track.Key;
        _ = Task.Run(async () =>
        {
            Lyrics lyrics;
            try
            {
                lyrics = await _provider.GetAsync(track, fetch.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Write($"lyrics lookup failed for {track}: {e.Message}");
                lyrics = new Lyrics(LyricsStatus.Error);
            }
            _ui.Post(_ => OnLyrics(key, track, lyrics), null);
        });
    }

    void OnLyrics(string key, Track track, Lyrics lyrics)
    {
        if (key != _trackKey)
            return; // the song changed while we were looking
        _lyrics = lyrics;
        Log.Write($"{track}: {lyrics.Status} ({lyrics.Lines.Count} lines)");
        var note = lyrics.Status switch
        {
            LyricsStatus.Unsynced => "only unsynced lyrics exist for this one",
            LyricsStatus.NotFound => "no lyrics found",
            LyricsStatus.Instrumental => "instrumental ♪",
            LyricsStatus.Error => "couldn't reach the lyrics server",
            _ => null,
        };
        if (note is not null)
            Flash($"{track} · {note}");
        UpdateLine();
    }

    void Flash(string message)
    {
        _flash = message;
        _flashStarted = Stopwatch.GetTimestamp();
        UpdateLine();
    }

    // ---- actions ----

    void OnStartedAgain()
    {
        if (!_settings.LyricsShown)
            ToggleLyrics();
        Flash("Subtune is already running · find it in the tray");
    }

    void ToggleLyrics()
    {
        _settings.LyricsShown = !_settings.LyricsShown;
        _overlay.LyricsShown = _settings.LyricsShown;
        _settings.Save();
        RefreshMenu();
    }

    void ToggleMoveResize()
    {
        _overlay.Editing = !_overlay.Editing;
        if (!_overlay.Editing)
        {
            (_settings.X, _settings.Y, _settings.Width, _settings.Height) =
                (_overlay.Left, _overlay.Top, _overlay.Width, _overlay.Height);
            _settings.Save();
        }
        RefreshMenu();
    }

    void ToggleNextLine()
    {
        _settings.ShowNextLine = !_settings.ShowNextLine;
        _settings.Save();
        _overlay.Relayout();
        RefreshMenu();
        UpdateLine();
    }

    void Nudge(int ms)
    {
        _settings.OffsetMs += ms;
        _settings.Save();
        RefreshMenu();
        Flash($"Timing {_settings.OffsetMs / 1000.0:+0.00;-0.00;0.00}s");
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartsWithWindows)
            key.DeleteValue("Subtune", throwOnMissingValue: false);
        else
            key.SetValue("Subtune", $"\"{Environment.ProcessPath}\"");
    }

    static bool StartsWithWindows =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Subtune") is string;

    static void OpenLyricsFolder()
    {
        Directory.CreateDirectory(Paths.UserLyrics);
        Process.Start(new ProcessStartInfo(Paths.UserLyrics) { UseShellExecute = true });
    }

    void RefreshMenu()
    {
        _showLyrics.Checked = _settings.LyricsShown;
        _moveResize.Checked = _overlay.Editing;
        _showNext.Checked = _settings.ShowNextLine;
        _timing.Text = $"Timing ({_settings.OffsetMs / 1000.0:+0.00;-0.00;0.00}s)";
    }

    protected override void ExitThreadCore()
    {
        _fetch?.Cancel();
        _lineTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        _overlay.Dispose();
        _settings.Save();
        base.ExitThreadCore();
    }

    // ---- helpers ----

    static ToolStripMenuItem Item(string text, string? shortcut, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => onClick();
        return item;
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The app icon (embedded Subtune.ico) at the size the tray wants.</summary>
    static Icon CreateIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("Subtune.ico")!;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
