# The listening database

The plugin stores listening data in `jellyfin-harmonie.db`, a SQLite database
in the plugin's configuration folder. All IDs are Jellyfin GUIDs stored as 32
lowercase hex characters without dashes. All timestamps are ISO-8601 UTC
strings. All `*_ticks` columns are .NET ticks: 10,000,000 ticks per second.

## playback_events

One row per finished listen. Written when a client reports a stop, or when an
abandoned session is recovered from its checkpoint.

| Column | Description |
| --- | --- |
| `id` | Row id. |
| `user_id` | The listening user. |
| `item_id` | The track. |
| `started_utc` | When the play began. Null when the plugin saw only progress reports and no start. |
| `stopped_utc` | When the play ended. For recovered sessions, the last time the session was observed. |
| `start_position_ticks` | Track position at the first observation. |
| `end_position_ticks` | Track position at the end. |
| `max_position_ticks` | The furthest position reached during the play. |
| `active_listen_ticks` | Time spent actually listening, excluding pauses. Null when no start event was seen, since the time cannot be measured. |
| `seek_forward_count` | Forward jumps of 10 seconds or more beyond normal playback progress. |
| `seek_backward_count` | Backward jumps of 10 seconds or more. |
| `pause_count` | Transitions from playing to paused. |
| `is_early_skip` | 1 when active listening stayed under `min(30s, duration / 5)` and the track did not complete. |
| `duration_ticks` | The track's runtime. |
| `played_to_completion` | 1 when the end position reached the track end (within `min(10s, duration / 20)` live; a wider `duration / 5` margin for recovered sessions) and at least half the track was actively listened. The listening floor stops a seek to the end from counting. |
| `counted_as_play` | 1 when the listen counts toward play counts. Live stops take Jellyfin's own judgment, the same one that raises the user's play count. Recovered sessions use a stand-in: at least half the track actively listened, and either the position within 10 seconds of the end or 90% of the track listened. |
| `play_session_id` | The client's play session id, when it sent one. |
| `client_name` | The reporting client, such as `Finamp`. |
| `device_id` | The reporting device. |

## playback_sessions

The durable state of playback that has not finished yet, one row per session
and user. Updated while clients play (throttled to one write per 30 seconds,
tighter near the track end). Deleted when the session stops normally. Rows
older than 24 hours are converted into `playback_events` rows, so listens
survive clients that die without a stop signal and server restarts.

| Column | Description |
| --- | --- |
| `session_key` | The play session id, or `sessionId:itemId` when the client sent none. |
| `user_id` | The listening user. |
| `item_id` | The track. |
| `started_utc` | When the play began. Null when the plugin saw only progress. |
| `last_observed_utc` | The last time a progress report arrived. Decides when the row counts as abandoned. |
| `start_position_ticks` | Track position at the first observation. |
| `end_position_ticks` | Track position at the last observation. |
| `max_position_ticks` | The furthest position reached so far. |
| `active_listen_ticks` | Listening time so far, excluding pauses. Null when no start event was seen. |
| `seek_forward_count` | Forward jumps so far. |
| `seek_backward_count` | Backward jumps so far. |
| `pause_count` | Pauses so far. |
| `is_paused` | 1 when the session was paused at the last observation. Used when a resumed session continues from this row. |
| `duration_ticks` | The track's runtime. |
| `play_session_id` | The client's play session id, when it sent one. |
| `client_name` | The reporting client. |
| `device_id` | The reporting device. |

## bootstrap_activity

Jellyfin's own aggregate play totals, imported once when the database is
first created. Gives recommendations a starting point from activity that
happened before the plugin was installed.

| Column | Description |
| --- | --- |
| `user_id` | The user. |
| `item_id` | The track. |
| `last_played_utc` | Jellyfin's last-played time at import. |
| `play_count` | Jellyfin's play count at import. |
| `captured_at_utc` | When the import ran. Events stopped after this moment add to the play count; older ones are ignored to avoid counting a play twice. |

## favorite_tracks

The user's current favorite tracks, kept in sync with Jellyfin.

| Column | Description |
| --- | --- |
| `user_id` | The user. |
| `item_id` | The favorited track. |
| `observed_at_utc` | When the plugin last saw the favorite. |

## playlist_tracks

Which tracks sit in which user playlists, and since when. Each sync replaces
a playlist's rows while carrying the dates of tracks that were already there.

| Column | Description |
| --- | --- |
| `playlist_id` | The playlist. |
| `item_id` | The track. |
| `user_id` | The playlist's owner. |
| `first_seen_at_utc` | When the plugin first saw the track in this playlist. |
| `added_at_utc` | When the user added the track. Null for tracks that were already in the playlist when the plugin first imported it, since the real add time is unknown. |
| `last_seen_at_utc` | When the plugin last confirmed the track is still in the playlist. |

## metadata

Plugin bookkeeping as key–value pairs.

| Column | Description |
| --- | --- |
| `key` | The entry name. `listening_activity.bootstrap_completed_at` and `listening_activity.preference_bootstrap_completed_at` mark the one-time imports as done. |
| `value` | The entry value. |

## schema_migrations

Which schema migrations have run, written by the migration runner.

| Column | Description |
| --- | --- |
| `version` | The migration number. |
| `applied_at_utc` | When it was applied. |

## user_track_metrics (view)

Not a table: a view combining the tables above into one row per user and
track. The recommendation scorer reads it to build seeds for mixes.

| Column | Description |
| --- | --- |
| `user_id` | The user. |
| `item_id` | The track. |
| `play_count` | The bootstrap play count plus every `counted_as_play` event stopped after the bootstrap capture. |
| `last_played_utc` | The most recent play, from events or bootstrap, whichever is later. |
| `outcome_sample_count` | Events where the outcome is measurable: active listening time known and a positive duration. The denominator for completion and skip rates. |
| `completed_play_count` | Events with `played_to_completion = 1`. |
| `early_skip_count` | Events with `is_early_skip = 1`. |
| `active_listen_ticks` | Total active listening time across all events. |
| `last_completed_utc` | The most recent completed listen. |
| `last_early_skip_utc` | The most recent early skip. |
| `is_favorite` | 1 when the track is currently favorited. |
| `favorite_observed_utc` | When the favorite was last seen. |
| `playlist_count` | How many of the user's playlists contain the track. |
| `last_playlist_added_utc` | The most recent time the user added the track to a playlist. |
| `last_playlist_observed_utc` | The most recent playlist sync that saw the track. |
