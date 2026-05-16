# Development

## Local install on macOS

```bash
./scripts/install-local.sh
```

Builds the plugin, stops Jellyfin, copies the DLL into your Jellyfin data dir, and restarts Jellyfin. Logs in `~/Library/Application Support/jellyfin/log/`. The script builds the net9 variant only; CI builds both.

## Multi-targeting

The plugin targets `net8.0` (Jellyfin 10.10.x) and `net9.0` (Jellyfin 10.11.x) from a single source tree. Each release publishes one ZIP per ABI; `manifest.json` carries one version entry per ABI. The version string encodes the target ABI in its fourth component: `X.Y.Z.0` is 10.10, `X.Y.Z.1` is 10.11.

Cross-version differences live in `#if NET8_0` blocks, mostly for namespace moves between Jellyfin's data and database packages.

## REST endpoints

All under `/Plugins/Harmonie`, all require an authenticated Jellyfin user.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/Status` | Live view of harmonie's `/api/v1/status`. |
| `POST` | `/Status/Test` | Test connectivity against unsaved form values. |
| `GET` | `/Scan` | Current scan state. |
| `POST` | `/Scan?force=true\|false` | Trigger a scan. |
| `POST` | `/Playlists/{playlistId}/Refresh` | Refresh a single smart playlist. |
| `GET` | `/PathSuggestions` | Library paths from harmonie and Jellyfin, for the path-mappings UI. |

## Track matching

Harmonie identifies tracks by absolute filesystem path; Jellyfin identifies them by GUID. The plugin bridges them by:

1. Resolving each seed in harmonie via `GET /api/v1/tracks/resolve`. Tags first (`artist`, `album`, `title`, `track#`), path fallback.
2. Building an in-memory index of the Jellyfin audio library on every refresh: `(artist, album, title, track#)` to `BaseItem.Id`, plus path to `BaseItem.Id`.
3. Resolving each harmonie match against that index.

If tags are clean this works without any path config. If tags don't match, the plugin falls back to absolute path comparison, with optional user-configured prefix substitutions for cases where harmonie and Jellyfin see the library at different mount points.

## Releasing

Every push to `main` publishes a new release. The workflow at `.github/workflows/release.yml`:

1. Builds the plugin for both target frameworks.
2. Packages each into a Jellyfin ZIP with a `meta.json` declaring the right `targetAbi`.
3. Creates a GitHub Release named `v0.1.<run>` and attaches both ZIPs.
4. Commits the updated `manifest.json` back to `main`.

The version is `<base>.<run>.<abi_slot>`. The base is `BASE_VERSION` in `release.yml` (default `0.1`); the ABI slot is `0` for 10.10 and `1` for 10.11.

Bump `BASE_VERSION` to start a new minor or major line. The Actions UI also has a "Run workflow" button to trigger a release without a code change.
