# Jellyfin Harmonie plugin

A Jellyfin plugin that uses [harmonie](https://github.com/mxschll/harmonie) — an audio similarity service — to build dynamic playlists from your Jellyfin music library.

You drive the plugin entirely through playlist titles and contents in the Jellyfin web UI. Name a playlist with a `[HRMNY]` prefix and the plugin will fill in the rest.

## How it works

There's one rule: any playlist whose name starts with the configured prefix (default `[HRMNY]`) is managed by the plugin. The mode is implicit in the title:

| Title                          | Mode  | What happens                                                                                          |
| ------------------------------ | ----- | ----------------------------------------------------------------------------------------------------- |
| `[HRMNY] Workout`              | seed  | Tracks you put in are seeds; harmonie returns similar tracks and the plugin appends them.            |
| `[HRMNY drift] Long mix`       | drift | One seed only. Each new track's anchor is the previous one — the playlist walks away from the seed.  |
| `[HRMNY drift=10] Long mix`    | drift | Same as drift, with chunk size 10 (larger = stay closer to the seed; smaller = drift faster).         |
| `[HRMNY energy=80] Banger mix` | energy | No seed needed. Harmonie ranks the library by danceability around your energy value, then shuffles. |

You can append `n=<count>` anywhere to set the total length, e.g. `[HRMNY drift=8 n=40]`. Default is 30. Energy is `0–100`.

The plugin remembers which tracks it added on the last refresh (in a small JSON file in the plugin config dir). On the next refresh:

- Items in the playlist that the plugin **did** add → previous matches; dropped.
- Items the plugin **didn't** add → seeds; preserved.

So adding or removing seeds works naturally, and the playlist always equals `seeds + fresh harmonie matches`, in that order. (Energy mode has no seeds, so the playlist is just the matches.)

## Refreshing

Three triggers, in increasing levels of automation:

1. **Auto** — when you add or remove a track in a `[HRMNY]` playlist, the plugin debounces for 5 seconds and then refreshes that playlist on its own. This is the normal way to use it.
2. **Manual single** — `POST /Plugins/Harmonie/Playlists/{playlistId}/Refresh`. Useful for shortcuts and bookmarklets.
3. **Manual all** — Plugins → Harmonie → "Refresh smart playlists now". Or use the daily scheduled task "Refresh Harmonie Playlists".

## Installation

The easiest way is to add this repository to Jellyfin's plugin sources.

1. In Jellyfin, open Dashboard → Plugins → Repositories → **+** (Add Repository).
2. **Repository URL**:

   ```
   https://raw.githubusercontent.com/<your-github-user>/jellyfin-harmonie/main/manifest.json
   ```

   Replace `<your-github-user>` with the GitHub account that owns the fork/clone. The default Repository Name can be anything (e.g. "Harmonie").
3. Save. Open Dashboard → Plugins → Catalog → and you'll see "Harmonie" under the Music category. Click Install.
4. Restart Jellyfin when prompted.
5. Open Plugins → Harmonie. Set the harmonie URL and API key. Click "Test connection".

### Manual install (no repo)

Download the latest release ZIP from [Releases](https://github.com/<your-github-user>/jellyfin-harmonie/releases), extract it into:

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

So a single source tag like `v0.1.0` produces `0.1.0.0` (10.10) and `0.1.0.1` (10.11). Both come from the same code; only the conditional namespace and trigger-info constants differ.

## Track matching

Harmonie identifies tracks by absolute filesystem path; Jellyfin identifies them by GUID. The plugin bridges them by:

1. Looking up each seed in harmonie via `POST /api/v1/tracks/lookup` (tags first, path fallback).
2. Building an in-memory index of the Jellyfin audio library on every refresh: `(artist, album, title, track#)` → `BaseItem.Id`, plus path → `BaseItem.Id` as a fallback.
3. Resolving each harmonie match against that index.

If your tags are clean this works without any path config. If MBID or tags can't be matched, the plugin falls back to absolute path comparison, optionally with the user-configured prefix substitutions for cases where harmonie and Jellyfin see the library at different mount points.

## REST endpoints

All require an authenticated Jellyfin user. Mounted under `/Plugins/Harmonie`.

| Method | Path                                       | Purpose                                                              |
| ------ | ------------------------------------------ | -------------------------------------------------------------------- |
| `GET`  | `/Status`                                  | Round-trip harmonie's `/status` (version, backend, indexed track count). |
| `POST` | `/Refresh`                                 | Refresh every prefix-mode playlist. Returns immediately.             |
| `POST` | `/Playlists/{playlistId}/Refresh`          | Refresh a single prefix-mode playlist.                               |

## Limitations

- Drift mode uses only the *first* seed in the playlist; harmonie's drift mode takes a single seed by design.
- Energy mode is shuffled (harmonie's `shuffle: true`).
- A track is excluded from a refresh if no Jellyfin item matches by tags or (mapped) path. Watch the Jellyfin log for unresolved counts.

## Releasing

Releases are automated via GitHub Actions. To cut a new version:

```bash
git tag v0.1.1
git push origin v0.1.1
```

The workflow at `.github/workflows/release.yml` runs on tag push and:

1. Builds the plugin with the tagged version.
2. Packages the DLL and a generated `meta.json` into `jellyfin-harmonie_<version>.zip`.
3. Computes the ZIP's MD5.
4. Creates a GitHub Release and attaches the ZIP.
5. Updates `manifest.json` on `main` with the new version entry pointing at the Release asset.

Within a few minutes, Jellyfin instances pointed at the manifest URL will see the new version in their plugin catalog (auto-update kicks in if enabled).

Tag format is `vX.Y.Z` or `vX.Y.Z.W`. The workflow normalises to a 4-part Jellyfin version internally.

## License

GPL-3.0, matching Jellyfin itself.
