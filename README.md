<p align="center">
  <img src="Jellyfin.Plugin.Harmonie/thumb.png" width="110" alt="Harmonie logo" />
</p>

<h1 align="center">Jellyfin Harmonie</h1>

<p align="center">
  <a href="https://github.com/mxschll/jellyfin-harmonie/actions/workflows/ci.yml">
    <img src="https://github.com/mxschll/jellyfin-harmonie/actions/workflows/ci.yml/badge.svg" alt="CI" />
  </a>
</p>

> [!NOTE]
> **Feedback wanted.** If anything in the install steps, settings, or playlist behaviour gets in your way, please open an issue. I want setup to be as painless as possible.

Spotify has Song Radio and Daily Mix. Plex has Sonic Sage. They listen to a track or your recent plays and surface dozens of similar songs from your library. Jellyfin doesn't have anything like that.

Harmonie fills that gap. It's a Jellyfin plugin that automatically generates playlists from your library based on audio similarity and your listening history, using [harmonie](https://github.com/mxschll/harmonie) for the analysis. Matches by audio embeddings. Lives natively within Jellyfin.

<p align="center">
  <img src="docs/playlists.png" alt="Harmonie playlists in the Jellyfin web UI" width="720" />
</p>

## Install

The plugin needs a running [harmonie](https://github.com/mxschll/harmonie) service to talk to. Install harmonie first, then the plugin.

### 1. Install harmonie

Install pipx, then harmonie itself:

```bash
sudo apt install pipx
pipx ensurepath

pipx install --pip-args='--pre' 'git+https://github.com/mxschll/harmonie.git'
HARMONIE_LIBRARIES=/path/to/music harmonie serve
```

Point it at the same music directories Jellyfin reads. The first scan starts automatically. See the [harmonie README](https://github.com/mxschll/harmonie#install) for everything else.

### 2. Install the plugin

In Jellyfin go to Dashboard > Catalog > Repositories (gear icon), and add this URL:

```
https://raw.githubusercontent.com/mxschll/jellyfin-harmonie/main/manifest.json
```

Open the Catalog tab, find Harmonie under Music, and click Install. **Restart Jellyfin**. Then open Plugins > Harmonie, and point the plugin at your harmonie server. Harmonie listens on port `8842` by default, so if you ran it on the same machine as Jellyfin the URL is `http://localhost:8842`. Save the form.

The plugin's settings page shows live harmonie scan progress:

<p align="center">
  <img src="docs/scan-progress.png" alt="Harmonie scan progress in the Jellyfin plugin settings, showing state, phase, and per-stage counters" width="720" />
</p>

## Use it

Make a normal Jellyfin playlist with one of these prefixes. The plugin refreshes the contents in the background.

**`[RADIO]` - radio mix:** The first N tracks (default 5, configurable) are seeds; the rest is filled with audio-similar tracks. Drag a track to the top to make it a seed; remove it to demote. The first seed is the strongest anchor.

**`[DRIFT]` - long evolving mix:** One seed (the first track) and the playlist walks away from it in chunks. Each chunk re-anchors on the last pick of the previous one, so the style evolves across the mix.

**`[GENRE] X` / `[STYLE] X`:** Fills with 100 tracks (configurable) of one Discogs genre or style. The text after the prefix is the filter: `[GENRE] Hip Hop` returns hip-hop tracks; `[STYLE] House` returns house tracks across every genre. The playlist regenerates daily with a new seed, so it feels different daily. See [docs/discogs-styles.md](docs/discogs-styles.md) for the full list of accepted values.

**`[MIX]` - daily mix from listening history:** Auto-fills from what you've played in the last week. You don't add tracks; anything you do add gets wiped. Default is "today's mix" — flip with tokens for "heavy rotation" or "stretch" variants.

You can override settings per playlist with tokens inside the brackets:

| Token | Mode | What it does |
| --- | --- | --- |
| `n=N` | any | playlist length, 1 to 500 |
| `days=N` | mix | listening window, 1 to 365 |
| `top` or `top=N` | mix | seed by play count rank instead of recency |
| `drift` | mix | use drift mode for the expansion |
| `style_min=F` | style, genre | minimum classifier probability, 0.0 to 1.0. Defaults to 0.6 (configurable in plugin settings) |

Examples:

- `[RADIO n=40] Workout`
- `[DRIFT n=50] Long Mix`
- `[MIX top days=30] Heavy Rotation`
- `[MIX drift] Stretch Mix`
- `[GENRE] Electronic`
- `[STYLE n=200] House`
- `[STYLE style_min=0.5] Hard Techno`

## Personal Mix playlists

The plugin maintains a fixed number of `Personal Mix · Style` playlists per user, one per top style derived from listening history. As your taste shifts the playlists rename and refill themselves. Enabled by default. Can be turned off in the plugin settings under "Personal Mix playlists".

## Song Radio / Instant Mix

When you tap "Instant Mix" in the Jellyfin web UI (or "Song Radio" in Finamp) on a track, the plugin returns audio-similar tracks instead of random tracks matching the seed's genre. Works in every Jellyfin client without setup. Falls back to Jellyfin's default genre-based behaviour when harmonie is unreachable or the track isn't in its index, so the button always works. Toggle off in plugin settings under "Instant Mix / Song Radio".

## Refresh

The plugin refreshes a playlist shortly after you edit it. Two scheduled tasks run in the background (Dashboard, Scheduled Tasks):

* **Refresh Harmonie Playlists:** daily at 03:00. Rebuilds every `[RADIO]`, `[DRIFT]`, `[MIX]`, `[STYLE]`, and `[GENRE]` playlist.
* **Refresh Harmonie Personal Mix Playlists:** every 30 days. Rebuilds the per-user Personal Mix playlists. 

Both schedules can be changed from the same page, and either can be triggered manually.

## Compatibility

Tested on Jellyfin 10.10 and 10.11.

## License

GPL-3.0.