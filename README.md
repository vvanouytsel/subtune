# Subtune

Synced song lyrics as a subtitle-style overlay, so you can follow along while gaming without
alt-tabbing to Spotify.

- Follows Spotify through Windows' own media controls, so **no Spotify login** is needed
- Time-synced lyrics from [LRCLIB](https://lrclib.net), cached locally, plus your own `.lrc` files
- Transparent, always on top, and **click-through**: the game keeps getting your mouse
- Resizable: the text scales with the box
- Light: event-driven, wakes up once per lyric line; about 0.3% of one CPU core and ~17 MB of private memory

## Controls

Hotkeys work from inside games. If another app already owns a combo, Subtune falls back to the
same combo with Shift added (the tray menu shows which one you got).

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+L` | Show / hide lyrics |
| `Ctrl+Alt+M` | Move / resize mode: drag to move, drag edges to resize, press again to lock |
| `Ctrl+Alt+Right` | Lyrics sooner (+0.25s) |
| `Ctrl+Alt+Left` | Lyrics later (-0.25s) |

Everything is also in the tray icon's menu (left-click the icon to show or hide). The menu also has
"Show next line", "Open my lyrics folder", "Start with Windows" and "Quit".

The overlay fades out when playback is paused and when a song has no lyrics.

## Games and fullscreen

Works over games in **borderless** or **windowed** mode. Games in **exclusive fullscreen** draw over
every other window, so switch those to borderless. Subtune is an ordinary window and never touches
the game process, so anti-cheat has nothing to object to.

## Missing lyrics

LRCLIB is community-run, so less mainstream songs may be missing. Drop your own file into the
lyrics folder (tray menu → "Open my lyrics folder") named exactly:

```
<Artist> - <Title>.lrc
```

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10 1809 or later.

```bash
dotnet run --project src/Subtune
```

Try it without Spotify (fake-plays a known song, optionally starting N seconds in):

```bash
dotnet run --project src/Subtune -- --demo 26
```

Single-file build (needs the .NET 10 Desktop Runtime on the machine that runs it):

```bash
dotnet publish src/Subtune -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Files

| What | Where |
|---|---|
| Settings | `%APPDATA%\Subtune\settings.json` |
| Your `.lrc` files | `%APPDATA%\Subtune\lyrics\` |
| Lyrics cache, log | `%LOCALAPPDATA%\Subtune\` |

Set `"AnyPlayer": true` in settings to follow other media apps when Spotify isn't running.
