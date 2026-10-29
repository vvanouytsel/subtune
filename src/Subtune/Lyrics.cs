using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Subtune;

enum LyricsStatus { Synced, Unsynced, Instrumental, NotFound, Error }

sealed record LyricLine(TimeSpan Time, string Text);

sealed class Lyrics(LyricsStatus status, IReadOnlyList<LyricLine>? lines = null)
{
    public LyricsStatus Status { get; } = status;
    public IReadOnlyList<LyricLine> Lines { get; } = lines ?? [];

    public static Lyrics FromLrc(string lrc) => new(LyricsStatus.Synced, Lrc.Parse(lrc));

    /// <summary>Index of the line being sung at <paramref name="time"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan time)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= time) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}

static partial class Lrc
{
    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex Timestamp();

    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Timestamp().Matches(raw);
            if (stamps.Count == 0)
                continue; // metadata like [ar:...] or plain text
            var text = Timestamp().Replace(raw, "").Trim();
            foreach (Match m in stamps)
            {
                var seconds = int.Parse(m.Groups[1].Value) * 60
                    + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    public static string Format(IEnumerable<LyricLine> lines) =>
        string.Join('\n', lines.Select(l =>
            $"[{(int)l.Time.TotalMinutes:00}:{l.Time.Seconds:00}.{l.Time.Milliseconds / 10:00}]{l.Text}"));
}

/// <summary>
/// Finds time-synced lyrics: the user's own .lrc files first, then the local cache, then LRCLIB
/// (a free, open lyrics database). Results are cached so each song is looked up once.
/// </summary>
sealed partial class LyricsProvider
{
    static readonly HttpClient Http = CreateClient();

    public async Task<Lyrics> GetAsync(Track track, CancellationToken ct)
    {
        if (FromUserFolder(track) is { } own)
            return own;
        if (FromCache(track) is { } cached)
            return cached;

        var lyrics = await FetchAsync(track, ct);
        if (lyrics.Status is LyricsStatus.Synced or LyricsStatus.Instrumental)
            WriteCache(track, lyrics);
        return lyrics;
    }

    // ---- the user's own files: "<Artist> - <Title>.lrc" in the lyrics folder ----

    static Lyrics? FromUserFolder(Track track)
    {
        foreach (var title in new[] { track.Title, SimplifyTitle(track.Title) }.Distinct())
        {
            var path = Path.Combine(Paths.UserLyrics, SafeFileName($"{track.Artist} - {title}.lrc"));
            if (File.Exists(path))
            {
                var lyrics = Lyrics.FromLrc(File.ReadAllText(path));
                if (lyrics.Lines.Count > 0)
                    return lyrics;
            }
        }
        return null;
    }

    static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---- cache ----

    static string CachePath(Track track)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(track.Key)));
        return Path.Combine(Paths.Cache, hash + ".json");
    }

    static Lyrics? FromCache(Track track)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath(track)));
            var status = Enum.Parse<LyricsStatus>(doc.RootElement.GetProperty("status").GetString()!);
            return status == LyricsStatus.Synced
                ? Lyrics.FromLrc(doc.RootElement.GetProperty("lrc").GetString()!)
                : new Lyrics(status);
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    static void WriteCache(Track track, Lyrics lyrics)
    {
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            var json = JsonSerializer.Serialize(new { status = lyrics.Status.ToString(), lrc = Lrc.Format(lyrics.Lines) });
            File.WriteAllText(CachePath(track), json);
        }
        catch (IOException e)
        {
            Log.Write($"couldn't cache lyrics: {e.Message}");
        }
    }

    // ---- LRCLIB ----

    static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Subtune/0.1 (Windows lyrics overlay)");
        return client;
    }

    static async Task<Lyrics> FetchAsync(Track track, CancellationToken ct)
    {
        JsonElement? exact = null;
        if (track.Duration > TimeSpan.Zero)
        {
            exact = await GetJsonAsync("get" + Query(
                ("track_name", track.Title), ("artist_name", track.Artist),
                ("album_name", track.Album), ("duration", ((int)Math.Round(track.Duration.TotalSeconds)).ToString())), ct);
            if (exact is { } e && (HasSynced(e) || IsInstrumental(e)))
                return ToLyrics(e);
        }

        // Spotify titles often carry extras ("- Remastered 2011", "(feat. X)") that the database lacks.
        var attempts = new[] { (track.Title, track.Artist), (SimplifyTitle(track.Title), FirstArtist(track.Artist)) }.Distinct();
        foreach (var (title, artist) in attempts)
        {
            var results = await GetJsonAsync("search" + Query(("track_name", title), ("artist_name", artist)), ct);
            if (results is { ValueKind: JsonValueKind.Array } list && BestMatch(list, track.Duration) is { } match)
                return ToLyrics(match);
        }
        return exact is { } fallback ? ToLyrics(fallback) : new Lyrics(LyricsStatus.NotFound);
    }

    static async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(path, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                if (response.IsSuccessStatusCode)
                {
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
                    return doc.RootElement.Clone();
                }
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                    response.EnsureSuccessStatusCode();
                // 5xx / 429: LRCLIB is busy; back off and retry.
            }
            catch (HttpRequestException) when (attempt < 3) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 3) { } // timeout
            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(1.5 * Math.Pow(2, attempt)), ct);
        }
        throw new HttpRequestException("LRCLIB is unreachable");
    }

    static string Query(params (string Key, string Value)[] pairs) =>
        "?" + string.Join('&', pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    static bool HasSynced(JsonElement e) =>
        e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0;

    static bool IsInstrumental(JsonElement e) =>
        e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;

    static double DurationOf(JsonElement e) =>
        e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;

    static JsonElement? BestMatch(JsonElement results, TimeSpan duration)
    {
        var synced = results.EnumerateArray().Where(HasSynced).ToList();
        if (synced.Count == 0)
            return null;
        if (duration <= TimeSpan.Zero)
            return synced[0];
        // Prefer the version whose length matches; a different cut has different timings.
        var closest = synced.MinBy(e => Math.Abs(DurationOf(e) - duration.TotalSeconds));
        return Math.Abs(DurationOf(closest) - duration.TotalSeconds) <= 5 ? closest : synced[0];
    }

    static Lyrics ToLyrics(JsonElement e)
    {
        if (HasSynced(e))
            return Lyrics.FromLrc(e.GetProperty("syncedLyrics").GetString()!);
        if (IsInstrumental(e))
            return new Lyrics(LyricsStatus.Instrumental);
        if (e.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String)
            return new Lyrics(LyricsStatus.Unsynced);
        return new Lyrics(LyricsStatus.NotFound);
    }

    [GeneratedRegex(@"\s*[\(\[](feat|ft|with)\.?\s.*?[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s+-\s+.*\b(remaster(ed)?|version|edit|live|mix|mono|stereo|deluxe)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@",|&| feat\.? | x ", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    static string SimplifyTitle(string title) => VersionSuffix().Replace(Featuring().Replace(title, ""), "").Trim();

    static string FirstArtist(string artist) => ArtistSeparator().Split(artist, 2)[0].Trim();
}
