using System.Diagnostics;
using Windows.Media.Control;

namespace Subtune;

sealed record Track(string Artist, string Title, string Album, TimeSpan Duration)
{
    /// <summary>Identifies the song; duration is left out because players sometimes report it late.</summary>
    public string Key => $"{Artist}\n{Title}\n{Album}";

    public override string ToString() => $"{Artist} - {Title}";
}

interface INowPlaying
{
    /// <summary>Raised (on any thread) when the track, play state or position jumps.</summary>
    event Action? Changed;

    Task StartAsync();

    (Track? Track, TimeSpan Position, bool Playing) Snapshot();
}

/// <summary>
/// Follows Spotify through Windows' media controls (the same info the volume flyout shows),
/// so no Spotify login is needed. Event-driven: nothing runs while nothing changes.
/// </summary>
sealed class MediaWatcher(bool anyPlayer) : INowPlaying
{
    static readonly TimeSpan JumpThreshold = TimeSpan.FromMilliseconds(400);

    readonly Lock _gate = new();
    readonly SemaphoreSlim _refreshing = new(1, 1);
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _sessionId;
    System.Threading.Timer? _safetyNet;

    Track? _track;
    TimeSpan _position;
    long _stamp;
    bool _playing;

    public event Action? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Refresh();
        _manager.CurrentSessionChanged += (_, _) => Refresh();
        // Media events occasionally go missing; a cheap re-check every few seconds covers that.
        _safetyNet = new System.Threading.Timer(_ => Refresh(), null, 5000, 5000);
        await RefreshAsync();
    }

    public (Track? Track, TimeSpan Position, bool Playing) Snapshot()
    {
        lock (_gate)
            return (_track, Interpolated(), _playing);
    }

    TimeSpan Interpolated() => _playing ? _position + Stopwatch.GetElapsedTime(_stamp) : _position;

    void Refresh() => _ = RefreshAsync();

    async Task RefreshAsync()
    {
        if (!await _refreshing.WaitAsync(0))
            return; // a refresh is already running and will see the latest state
        try
        {
            var session = PickSession();
            Follow(session);
            if (session is null)
            {
                Publish(null, TimeSpan.Zero, false);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            bool playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position;
            if (playing)
            {
                // Spotify only refreshes the timeline on play/pause/seek, so add the time since then.
                var age = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (age > TimeSpan.Zero && age < duration)
                    position += age;
            }

            var track = string.IsNullOrWhiteSpace(props.Title)
                ? null
                : new Track(props.Artist ?? "", props.Title, props.AlbumTitle ?? "", duration);
            Publish(track, position, playing);
        }
        catch (Exception e)
        {
            Log.Write($"media refresh failed: {e.Message}");
        }
        finally
        {
            _refreshing.Release();
        }
    }

    void Publish(Track? track, TimeSpan position, bool playing)
    {
        lock (_gate)
        {
            bool jumped = (position - Interpolated()).Duration() > JumpThreshold;
            if (track?.Key == _track?.Key && playing == _playing && !jumped)
            {
                _track = track; // pick up a late duration without disturbing the clock
                return;
            }
            _track = track;
            _position = position;
            _stamp = Stopwatch.GetTimestamp();
            _playing = playing;
        }
        Changed?.Invoke();
    }

    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
            return null;
        var sessions = _manager.GetSessions();
        var spotify = sessions.FirstOrDefault(s =>
            s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase));
        return spotify ?? (anyPlayer ? _manager.GetCurrentSession() : null);
    }

    // Subscribe to the chosen session's events (and drop the previous one's).
    void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        var id = session?.SourceAppUserModelId;
        if (id == _sessionId)
            return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.TimelinePropertiesChanged -= OnTimeline;
            _session.PlaybackInfoChanged -= OnPlayback;
        }
        _session = session;
        _sessionId = id;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaProperties;
            session.TimelinePropertiesChanged += OnTimeline;
            session.PlaybackInfoChanged += OnPlayback;
            Log.Write($"following {id}");
        }
    }

    void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Refresh();
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Refresh();
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Refresh();
}

/// <summary>Pretends a well-known song is playing, to try the overlay without touching Spotify.</summary>
sealed class DemoPlayer(double startSeconds) : INowPlaying
{
    static readonly Track Song = new("Queen", "Bohemian Rhapsody", "A Night At The Opera", TimeSpan.FromSeconds(354));

    readonly long _start = Stopwatch.GetTimestamp() - (long)(startSeconds * Stopwatch.Frequency);

    public event Action? Changed;

    public Task StartAsync()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public (Track? Track, TimeSpan Position, bool Playing) Snapshot() =>
        (Song, Stopwatch.GetElapsedTime(_start), true);
}
