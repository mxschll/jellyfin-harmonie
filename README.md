# Jellyfin Harmonie plugin

A Jellyfin plugin that uses [harmonie](https://github.com/mxschll/harmonie) — an audio similarity service — to build dynamic playlists from your Jellyfin music library.

You drive the plugin entirely through playlist titles and contents in the Jellyfin web UI. Name a playlist `[RADIO]` or `[DRIFT]` and the plugin fills in the rest.

## How it works

The plugin recognises three kinds of playlists by their name prefix:

| Title                       | Mode  | Seeds                                    | What happens                                                                                            |
| --------------------------- | ----- | ---------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| `[RADIO] Workout`           | radio | the first **N** tracks in the playlist (set in plugin settings; default 5) | harmonie returns similar tracks and the plugin appends them after the seeds. The first track is the strongest anchor. To make any other track a seed, drag it into the top N. |
| `[DRIFT] Long mix`          | drift | one track you put in                     | each new chunk's anchor is the previous one — the playlist walks gradually away.                       |
| `[MIX] My Mix`              | mix   | your Jellyfin listening history          | seeds itself; you don't add tracks. Anything you do add is wiped on the next refresh.                  |

You can append tokens inside the brackets to override settings per playlist:

| Token              | Modes        | What it does                                       |
| ------------------ | ------------ | -------------------------------------------------- |
| `n=<count>`        | all          | playlist length (1–500)                           |
| `days=<count>`     | mix          | listening window in days (1–365)                  |
| `top` / `top=<N>`  | mix          | seed by play count rank instead of recency        |
| `drift`            | mix          | use harmonie's drift mode for the expansion        |

Examples:

- `[RADIO n=40] Workout` — 40 similar tracks
- `[DRIFT n=50] Long mix` — 50-track drifting walk
- `[MIX]` — defaults from settings (recent plays, last 7 days, 30 tracks)
- `[MIX top days=30] Heavy Rotation` — your top-played tracks of the month
- `[MIX drift] Stretch Mix` — gradually evolves from your recent plays

For Radio and Drift, the plugin owns everything below the seeds. Each refresh reads the seeds from the top of the playlist (first N for radio, first 1 for drift), wipes the rest, and re-fills with fresh harmonie matches. Add a track and it lands at the bottom — drag it up to make it a seed. Remove the seed from the top and the next track in line becomes a seed.

Mix is different: every refresh wipes the playlist and replaces it from scratch. The "seeds" are derived from your listening history — you never add them by hand.

## Per-user style cluster playlists (optional)

When the *style cluster playlists* feature is enabled in plugin settings, the daily refresh maintains an additional set of `[STYLE]` playlists for each Jellyfin user, one per top style derived from that user's listening history.

How it works:

1. Pull the user's top-played tracks in the configured window (default 30 days).
2. Resolve each against harmonie and read its style classification.
3. Vote: each track contributes its top-1 style. Top-N counted styles win.
4. For each top style, maintain one `[STYLE] Genre · Style` playlist seeded from the same recent listens, filtered to that style by harmonie.

The playlists are *managed* — the plugin owns them. As your taste shifts, the playlist at slot 0 just renames itself to your current top style, instead of leaving stale playlists behind. Reducing the count in settings removes the excess; deleting one yourself is fine — the plugin recreates it on the next refresh.

Turn it on under **Plugins → Harmonie → [STYLE] cluster playlists**.

## Refreshing

Three triggers, in increasing levels of automation:

1. **Auto** — when you add or remove a track in a smart playlist, the plugin debounces for 5 seconds and then refreshes that playlist on its own. This is the normal way to use it.
2. **Manual single** — `POST /Plugins/Harmonie/Playlists/{playlistId}/Refresh`.
3. **Manual all** — run the daily scheduled task "Refresh Harmonie Playlists" from Dashboard → Scheduled Tasks.

## Installation

1. In Jellyfin, open Dashboard → Plugins → Repositories → **+** (Add Repository).
2. **Repository URL**:

   ```
   https://raw.githubusercontent.com/mxschll/jellyfin-harmonie/main/manifest.json
   ```

3. Save. Open Dashboard → Plugins → Catalog → and you'll see "Harmonie" under the Music category. Click Install.
4. Restart Jellyfin when prompted.
5. Open Plugins → Harmonie. Set the harmonie URL and API key. Click "Test connection".

### Manual install (no repo)

Download the latest release ZIP from [Releases](https://github.com/mxschll/jellyfin-harmonie/releases), extract it into:

```
<jellyfin-data>/plugins/Jellyfin.Plugin.Harmonie_<version>/
```

…and restart Jellyfin.

### Local development

For iterating on the plugin against a local Jellyfin install on macOS:

```bash
./scripts/install-local.sh
```

The script builds, stops Jellyfin, copies the DLL into your Jellyfin data dir, and restarts Jellyfin. Logs show up in `~/Library/Application Support/jellyfin/log/`.

The plugin multi-targets `net8.0` (Jellyfin 10.10.x) and `net9.0` (Jellyfin 10.11.x). The local script builds only the net9 variant; the GitHub Actions release workflow builds both.

## Compatibility

| Jellyfin version | Plugin target | .NET runtime |
| --- | --- | --- |
| 10.10.x | net8.0  | .NET 8 |
| 10.11.x | net9.0  | .NET 9 |

Each release publishes two ZIPs (one per ABI) attached to the same GitHub release, and `manifest.json` carries one version entry per ABI. Jellyfin picks the right one for the server's version automatically.

The plugin's version string encodes the target ABI in its fourth component:
- `X.Y.Z.0` → built for Jellyfin 10.10
- `X.Y.Z.1` → built for Jellyfin 10.11

## Track matching

Harmonie identifies tracks by absolute filesystem path; Jellyfin identifies them by GUID. The plugin bridges them by:

1. Resolving each seed in harmonie via `GET /api/v1/tracks/resolve` (tags first, path fallback).
2. Building an in-memory index of the Jellyfin audio library on every refresh: `(artist, album, title, track#)` → `BaseItem.Id`, plus path → `BaseItem.Id` as a fallback.
3. Resolving each harmonie match against that index.

If your tags are clean this works without any path config. If tags can't be matched, the plugin falls back to absolute path comparison, optionally with the user-configured prefix substitutions for cases where harmonie and Jellyfin see the library at different mount points.

## REST endpoints

All require an authenticated Jellyfin user. Mounted under `/Plugins/Harmonie`.

| Method | Path                                       | Purpose                                                              |
| ------ | ------------------------------------------ | -------------------------------------------------------------------- |
| `GET`  | `/Status`                                  | Combined view of harmonie's `/info` and `/stats`. Used by the config page's "Test connection" button. |
| `POST` | `/Playlists/{playlistId}/Refresh`          | Refresh a single smart playlist.                                     |

## Limitations

- Drift uses only the *first* seed; harmonie's drift mode takes a single seed by design.
- A track is excluded from a refresh if no Jellyfin item matches by tags or (mapped) path. Watch the Jellyfin log for unresolved counts.

## Releasing

Releases are fully automated. **Every push to `main` that touches the plugin source publishes a new release**, and Jellyfin instances pointed at the manifest will see it within their next plugin-catalog refresh.

The workflow at `.github/workflows/release.yml`:

1. Builds the plugin for both target frameworks (net8.0 / net9.0).
2. Packages each into a Jellyfin-compatible ZIP with a `meta.json` declaring the right `targetAbi`.
3. Creates a GitHub Release named `v0.1.<run>` and attaches both ZIPs.
4. Commits the updated `manifest.json` back to `main`.

The version is auto-derived: `<base>.<github_run_number>.<abi_slot>`, where the base is the `BASE_VERSION` env (`0.1` by default) and the ABI slot is `0` for 10.10 / `1` for 10.11.

To bump the major/minor (e.g. for a 0.2 release), edit `BASE_VERSION` in `release.yml` and push.

You can also trigger a release manually from the Actions UI (`Run workflow` button) without changing any code.

## License

GPL-3.0, matching Jellyfin itself.
