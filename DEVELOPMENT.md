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

Versioning is tag-driven. Every push to `main` that touches plugin code computes the next semver tag from the most recent `v*.*.*`, pushes the new tag, builds both ABIs, attaches the ZIPs to a GitHub Release, and commits the updated `manifest.json` back to `main`.

Default bump is patch. Override on the **subject line** of the commit:

| Marker | Effect |
| --- | --- |
| `[bump minor]` | `X.Y.Z` → `X.(Y+1).0` |
| `[bump major]` | `X.Y.Z` → `(X+1).0.0` |
| `[skip release]` (or `[skip ci]`) | No tag, no release |

Subject-only matching is deliberate: markers in the body, code blocks, or a quoted PR description don't fire. For PR merges, put the marker in the squash commit's subject (the easiest is to include it in the PR title).

The Actions UI's **Run workflow** button takes a `level` input (`patch` / `minor` / `major`) for ad-hoc bumps without a code change.

The git tag is three-octet semver (`v1.0.0`); the per-ABI build appends a fourth octet for the Jellyfin manifest convention: `X.Y.Z.0` for net8.0/10.10, `X.Y.Z.1` for net9.0/10.11.
