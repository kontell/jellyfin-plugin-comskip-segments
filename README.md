# Comskip Commercial Segments (Jellyfin plugin)

Detects commercials in DVR recordings (and, on demand, existing library files) with an
external Comskip binary, and exposes the breaks as native Jellyfin **Commercial** media
segments so the built-in skip UI handles them. Modeled on how Emby's DVR does it, but
wired for Jellyfin's media-segment system.

## How it fits together

- **One detection queue, two triggers.** `RecordingWatcher` enqueues new recordings;
  `LibraryScanTask` enqueues whitelisted existing files. Both feed the same
  `DetectionManager`, which runs Comskip with a capped worker pool.
- **Detection is out of band.** `ComskipSegmentProvider` (the `IMediaSegmentProvider`)
  never runs Comskip during a scan — it only serves segments already computed and stored
  by the manager, so the Media Segment Scan stays fast. When a job finishes, the manager
  re-runs providers for that one item so results publish immediately.
- **Crash ≠ empty.** `DetectionStore` records "no commercials found" and "detection
  failed" differently, so a corrupt/truncated `.ts` is retried later instead of being
  marked done forever.
- **Read recordings, write a work dir.** Comskip is always invoked with an explicit
  `--output` work directory the service account owns; the read-only recordings folder is
  never written to.
- **Audio-format trim is the deliberate exception to that.** Some broadcast recordings
  open (and/or close) with a different audio channel layout than the movie itself — e.g.
  a network preview/bumper block in 2.0 stereo before a movie broadcast in 5.1 — which
  makes the whole file read as 2.0 even though the movie is 5.1, because Jellyfin probes
  audio format from the start of the file. `AudioTrim/AudioChannelScanner.cs` decodes the
  primary audio stream's per-frame channel count across the whole file via `ffprobe` and
  groups it into contiguous runs; the run containing the recording's own temporal midpoint
  is treated as the movie (this is what correctly survives DVR pre-roll padding that
  starts mid-way through a differently-formatted previous program, rather than just
  trusting whatever's at t=0). `AudioTrim/RecordingTrimmer.cs` then cuts everything
  outside that run with `ffmpeg -c copy` in the work directory first, and only once that
  output passes a sanity check does it swap the result into the recording's own path —
  the one place this plugin does write into the recordings folder, and only when this
  feature is explicitly turned on. Off by default; see "Audio format trim" below.

## Build

Requires the .NET SDK matching your server (net10.0 for the Jellyfin 12.0.x line).

```
dotnet publish -c Release
```

The build output DLL is `Jellyfin.Plugin.ComskipSegments.dll` under
`bin/Release/net10.0/`.

> Pin the `Jellyfin.Controller` / `Jellyfin.Model` versions in the `.csproj` to your
> exact server version (Dashboard → About) before building. A mismatch is the usual
> cause of the plugin failing to load.

## Install

### Option A: via a plugin repository (no build needed)

1. Dashboard → Plugins → **Repositories** → add:
   ```
   https://raw.githubusercontent.com/mackandall/jellyfin-plugin-comskip-segments/main/manifest.json
   ```
2. Dashboard → Plugins → **Catalog** → **Comskip Commercial Segments** → Install.
3. Restart Jellyfin.

### Option B: build and install manually

1. Find your Jellyfin plugins folder. On a Linux package install it's typically
   `/var/lib/jellyfin/plugins`, owned by the `jellyfin` service account — installing
   requires `sudo` (or write access as that user).
2. Create a subfolder and drop the DLL, `icon.png`, and `meta.json` in:
   ```
   sudo mkdir -p /var/lib/jellyfin/plugins/ComskipSegments
   sudo cp bin/Release/net10.0/Jellyfin.Plugin.ComskipSegments.dll icon.png meta.json /var/lib/jellyfin/plugins/ComskipSegments/
   ```
3. Restart Jellyfin.

### Either way

Dashboard → Plugins → **Comskip Commercial Segments** → set the paths → click
**Test paths**. This checks them *as the service account*, which catches the
`/home` traversal and systemd-sandbox (`ProtectHome`) cases the browser can't see.

### Browser overlay & item-menu buttons (optional, separate step)

The seek-bar commercial-marker overlay and the two item-details `...` menu entries
("Scan for commercials" and "Fix opening/closing audio format") are **not** wired up
just by installing the plugin DLL above — they're browser-side scripts that have to be
loaded by jellyfin-web's `index.html`, which the plugin can't patch itself (that file is
root-owned; the plugin runs as the `jellyfin` service account):

```
sudo Web/install-overlay.sh
# or, if your web root isn't the default /usr/share/jellyfin/web:
sudo Web/install-overlay.sh /path/to/jellyfin-web/index.html
```

Safe to re-run any time (each patch checks its own marker comment and only inserts if
missing). **Re-run it after every `jellyfin-web` package upgrade** — that package
overwrites `index.html` wholesale, silently dropping the patch. If that happens, the
plugin's own startup log will warn with "web patch is missing at ...". Hard-refresh the
browser tab after (re)running it.

Both menu entries only ever appear for users with the administrator role — regular
accounts never see them, regardless of this patch. "Fix opening/closing audio format"
also always appears once the patch is applied, even when **Enable audio-format trim**
is off in settings; clicking it just returns a "disabled in plugin settings" message in
that case rather than being hidden outright.

## Use

- **DVR:** leave "Automatically detect commercials on new recordings" on. New recordings
  under the recordings folder are queued automatically.
- **Existing files:** add absolute folders to "Extra scan folders", then run
  Dashboard → Scheduled Tasks → **Scan library for commercials (Comskip)** on demand.
  Comskip only runs automatically on files under the recordings folder and those extra
  folders; any other library (e.g. archives on a NAS) is never scanned unless you ask for
  it from an item's "..." menu.
- **Archives with an .edl already:** when a video has an `.edl` beside it (same name,
  `.edl` extension) written by another program — SageTV, Channels DVR, Emby, a standalone
  Comskip — the plugin uses it instead of running Comskip, automatically (on Jellyfin's
  Media Segment Scan) and for manual scans alike. Both EDL styles are understood:
  Comskip's `0` (cut) and Kodi/Channels-style `3` (commercial break). Such a file is never
  overwritten; "Also place a copy of the .edl next to the recording" only replaces `.edl`
  files this plugin wrote itself.
- **Library renamed or its folders edited:** Jellyfin deletes and re-creates every item
  of the library, which also deletes their segments. The plugin notices each re-added
  item that it has breaks for and publishes them again about 15 seconds later.

Skip behavior (Auto vs Ask) is Jellyfin's own setting, per user, under playback
preferences — this plugin only supplies the segments.

### Manual re-detect / clearing a bad result

The config page's "Manual re-detect" section (item ID + buttons) covers three cases for
one item at a time:

- **Run detection**, Force checked: clears any existing result and segments, then
  re-scans — bypassing the show/channel allow/block lists, same as a forced action
  everywhere else in this plugin.
- **Check status**: shows the stored result without changing anything.
- **Clear result (no re-scan)**: wipes the published segments and stored record only —
  no re-scan is queued. Use this when a result was produced by something that shouldn't
  have run in the first place (most commonly: a channel-cache entry that expired before
  a later automatic pass picked the item up anyway), where a forced re-run would just
  reproduce the same unwanted result.

### Audio format trim

Off by default (**Enable audio-format trim** in plugin settings). Once on, you can run
it two ways:

- **Automatically:** turn on **Apply automatically as recordings finish** too. Runs as
  part of the normal post-recording pipeline, before Comskip, so break timestamps come
  out correct against the final file from the start.
- **Per item, manually:** leave automatic apply off and either click **"Fix
  opening/closing audio format"** in an item's `...` action-sheet menu, or use the "Audio
  format trim" section's item-ID field and buttons on the plugin's config page. Good way
  to build confidence in the detector against your own channels before trusting it
  unattended. If Comskip had already published commercial-skip segments for that item,
  a trim that cuts anything off the front invalidates their timestamps — this is detected
  and handled automatically (segments cleared, then a normal, gate-respecting Comskip
  re-detect is queued — so a block-listed channel that a trim just touched stays
  block-listed rather than getting silently re-scanned as a side effect).

**Keep the original file as a backup** (on by default) renames the untouched original to
`<name>.original<ext>` next to the corrected file instead of deleting it.

## The one file to watch

`ComskipSegmentProvider.cs` is the only file touching the version-sensitive
media-segment API. If it doesn't compile against your server, match its three members to
`MediaBrowser.Controller/MediaSegments/IMediaSegmentProvider.cs` at your server's tag.
Everything else is plain .NET.

## Cutting a release

The repository manifest (`manifest.json`) and the GitHub Release it points to have to
be updated together, or Option A installs above break:

1. Bump `AssemblyVersion`/`FileVersion` in the `.csproj`, `dotnet build -c Release`.
2. Zip just the DLL + `icon.png` (not `meta.json` — the server writes its own from the
   manifest on a repository install) as
   `jellyfin-plugin-comskip-segments_<version>.zip`.
3. `gh release create v<version> <zip>` and note the asset's download URL.
4. In `manifest.json`, append a new entry to `versions` with that `sourceUrl`, the new
   `version`, and the zip's MD5 (`md5sum <zip>`) as `checksum`.
5. Add a line to Revision History below.

## Revision History

| Version | Date | Notes |
|---|---|---|
| 1.1.0.0 | 2026-09-28 | Skip commercials in recordings still in progress (web + TV forks), web commercial badges, "Watch from start" for programmes being recorded, use existing .edl files, AudioTrim writes MPEG-TS for .mpg, menu and live-scan fixes. Re-run Web/install-overlay.sh for the web features. |
| 1.0.1.0 | 2026-09-12 | Version bump only, no code changes. |
| 1.0.0.0 | 2026-09-12 | Initial public release. |

## Known follow-ups (not yet built)

- Duration sanity check (compare EDL end vs. `ffprobe` duration) to flag truncated
  recordings distinctly from clean short ones.
- Retry backoff/scheduling for `Failed` items.

## Repo metadata

GitHub's description and topics aren't stored in this repo, so they drift silently
after a rename/fork. Current canonical values, kept here so they can be diffed against
reality (`gh repo view --json description,repositoryTopics`):

- **Description:** Detect commercials in Jellyfin DVR recordings with Comskip and
  expose them as native MediaSegments
- **Topics:** `jellyfin`, `jellyfin-plugin`, `comskip`, `dvr`, `commercial-detection`,
  `media-segments`, `livetv`

## License

MIT — see [LICENSE](LICENSE).
