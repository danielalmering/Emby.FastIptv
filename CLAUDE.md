# Emby.FastIptv

M3U/IPTV tuner host with EPG support for Emby Server.

## Critical constraint
**Only plugin files may be modified.** Emby server DLLs and web-client JS files are read-only references. Do not alter server code.

---

### Project layout
```
Emby.FastIptv/
├── Plugin.cs                          # Plugin entry-point, registers embedded pages
├── PluginConfiguration.cs             # Global config (codecs, UA, cache TTL)
├── TunerSettingsEntry.cs              # Per-tuner overrides stored in plugin config
├── Properties/AssemblyInfo.cs         # Version — bump on every release
├── LiveTv/
│   ├── FastIptvTuner.cs               # ITunerHost implementation — core logic
│   ├── M3uParser.cs                   # Parses #EXTINF lines into M3uChannel
│   ├── M3uChannel.cs                  # M3U channel model
│   ├── XmlTvParser.cs                 # Parses XMLTV XML into ProgramInfo
│   ├── PassthroughLiveStream.cs       # No-op ILiveStream so Emby's recorder works (see gotchas)
│   └── Probe/                         # Fast in-process stream probe (see below)
│       ├── StreamProber.cs            # HTTP read budget/deadline, HLS resolution, result cache
│       ├── TsProbe.cs                 # MPEG-TS demux: PAT -> PMT -> per-PID elementary streams
│       ├── StreamTypes.cs             # PMT stream_type + descriptors -> ffmpeg codec name
│       ├── VideoParameterSets.cs      # H.264/HEVC SPS + MPEG-2 sequence header -> resolution
│       ├── AudioHeaders.cs            # ADTS/AC-3/MPEG-audio frame headers -> rate + channels
│       ├── Crc32Mpeg.cs               # PSI section CRC validation
│       ├── BitReader.cs               # Exp-Golomb bit reader
│       └── ProbedStream.cs            # Container-agnostic stream description
└── Configuration/
    ├── configurationpage.html         # Plugin-level settings page (stream defaults, global UA, cache TTL)
    ├── configurationcontroller.js     # Controller for configurationpage
    ├── tunersetup.html                # Per-tuner add/edit form
    └── tunersetupcontroller.js        # Controller for tunersetup
```

### Plugin identifiers
| Key | Value |
|-----|-------|
| Plugin GUID | `C7D8E9F0-A1B2-4C3D-8E4F-5A6B7C8D9E0F` |
| Tuner type string | `FastIptv` |
| Channel ID prefix | `fastiptv_` |
| SetupUrl | `configurationpage?name=FastIptvTunerSetup` (no `.html`) |
| Config file | `FastIptv.xml` |

### Emby routing rules (do not break)
- Embedded pages are served at `/configurationpage?name=<PageName>` — **no `.html`** in the URL
- `SetupUrl` on `ITunerHost` is used by `setuptab.js` when the user clicks "Add tuner"
- When editing, `approuter.js` appends `&id=<tunerId>` to `SetupUrl`
- Page controller receives `params.id` (or `params.Id`) for the tuner being edited
- AMD modules: `define(['baseView', 'loading', ...], function(BaseView, loading) { ... })`
- Always use `ApiClient.getJSON()` — never `ApiClient.ajax({type:'GET'})` (returns raw string, breaks `.filter()`)

### Per-tuner settings (TunerSettingsEntry)
Stored as `TunerSettings[]` array in `PluginConfiguration` (not a Dictionary — XmlSerializer can't handle it).

| Field | Purpose |
|-------|---------|
| `TunerId` | Matches `TunerHostInfo.Id` |
| `UserAgent` | Overrides global UA for this tuner's playlist + stream requests |
| `CustomHeaders` | Newline-separated `Name: Value` pairs |
| `CacheTtlHours` | 0 = use global default |
| `EpgUrl` | Optional XMLTV EPG URL for this tuner |
| `StreamTimeoutSeconds` | Stream connect timeout in seconds; 0 = use global default (15 s) |
| `StreamRetryCount` | Retries on stream open failure; 0 = use global default (2) |
| `EnableHealthProbe` | When true, sends a HEAD request before opening the stream |

### Phases

#### Phase 1 — Core parity ✅ DONE
- M3U fetch with custom User-Agent and HTTP headers per tuner
- Per-tuner channel list cache with configurable TTL
- Radio channel detection (`radio="true"` / `type="radio"` EXTINF attrs → `ChannelType.Radio`)
- Stable channel IDs: prefer `tvg-id`, fall back to sanitised channel name
- HLS stream detection (`.m3u8`/`.m3u` suffix → `container=hls`)
- Clean-up of per-tuner settings when a tuner is deleted
- Tuner setup UI: Name, M3U URL, User-Agent, Custom Headers, Cache TTL

#### Phase 2 — EPG / Guide Data ✅ DONE (v1.1.0.0)
- `SupportsGuideData(info)` → `true` when `TunerSettingsEntry.EpgUrl` is set
- `SupportsRemappingGuideData(info)` → always `true` (lets users add a separate Emby listing provider)
- `GetProgramsAsync`: fetches + parses XMLTV when EpgUrl is set; returns live-stream placeholder otherwise
- `XmlTvParser`: XMLTV timestamp parsing (`YYYYMMDDHHmmss ±HHMM`), episode numbers (`xmltv_ns`), genres, ratings, icons
- Per-tuner XMLTV EPG cache (same TTL as channel cache, invalidated on tuner save/delete)
- XMLTV channel matching: strips `fastiptv_` prefix from `ChannelInfo.Id` to get the raw tvg-id used in the XMLTV `channel` attribute
- `ImportGuideData: true` set on TunerHostInfo when saving
- Tuner setup UI: added XMLTV EPG URL field (null-guarded in JS)

#### Phase 3 — Stream quality / reliability ✅ DONE (v1.0.2.1)
- Stream health probe: `EnableHealthProbe` per tuner → HEAD request (5 s timeout) before opening stream
- `StreamRetryCount` / `StreamTimeoutSeconds` drove the old `HttpLiveStream` open; since v1.0.4.0 the
  live stream holds no connection, so these settings are stored but currently unused
- Channel `group-title` mapped to `ChannelInfo.Tags` so channels appear grouped in the Emby UI

#### Phase 4 — Fast stream probing ✅ DONE (v1.0.3.0)
Leaving `MediaStreams` empty is correct but makes Emby probe every channel on every tune, which is
what made channels slow to start. The plugin now determines the real stream details itself, in
process, in a few hundred milliseconds, and caches them.

- `StreamProber.ProbeAsync` fetches the stream once and stops as soon as it knows enough:
  codecs come from the PMT (first packets), resolution needs a key frame
- Deadlines: `ProbeTimeoutSeconds` (hard, default 4 s) and an internal 1200 ms soft deadline after
  which known codecs are accepted without resolution — Emby then applies native resolution
- Read budget `ProbeMaxKilobytes` (default 4096) is a ceiling, not a cost. Broadcast GOPs are long:
  on the NPO test channels the first SPS sits ~3.5 MB in, while the PMT is in the first KB
- Results cached in memory and in `<plugin data folder>/probe-cache.json`, TTL `ProbeCacheHours`
  (default 24). Failures are cached in memory only, for 15 min. `OnSaved` clears everything, so
  re-saving a tuner is the manual reset when a provider changes a channel's codec
- Concurrent tunes of the same URL share one probe (providers often allow one connection)
- HLS: single-variant playlists are followed to their first segment and parsed as MPEG-TS.
  Multi-variant masters and `#EXT-X-MAP` (fMP4) are rejected
- `AdvertiseStreamMetadata` still wins when enabled — it is the legacy escape hatch and must be
  **off** for the probe to run
- There is deliberately **no** "let Emby probe everything" switch. The probe already defers to
  Emby's probe per channel whenever it is not certain, so a global version of that would only be a
  slower route to the same answer. The config page is a two-way radio group (`streamMetadataMode`:
  fast probe / fixed codecs) stored in the single `AdvertiseStreamMetadata` boolean; each mode's own
  options only show while that mode is selected. Two checkboxes were wrong here — the second
  silently overrode the first

**The probe rejects rather than guesses.** A rejection costs only the time of Emby's own probe;
a wrong codec breaks playback outright. It rejects on: any PMT `stream_type` it cannot name
(an unnamed entry would shift every later ffmpeg stream index), multi-program transports,
scrambled payloads, non-MPEG-TS content, missing video payload, and video with bit depth > 8
(that is where HDR lives, and VUI/SEI colour parsing is deliberately not implemented — advertising
such a channel as SDR would suppress tone mapping).

`ProbedStream.Index` is the position in the PMT ES loop, which is the index ffmpeg assigns, because
Emby turns it into `-map 0:<index>`.

Verified against the live provider and against synthetic streams (`ffmpeg` + `ffprobe` as the
reference): h264 High/L4.2 1080p + AAC LC 48k stereo (3 live channels, 425–533 ms cold), HEVC Main
720p + AC-3 5.1, MPEG-2 576i + MP2 mono, h264 with two audio tracks and ISO-639 languages, HEVC
Main 10 4K (rejected as intended), multi-program (rejected), non-TS payload (rejected), and
single-variant HLS (34 ms via first segment).

#### Phase 5 — Advanced QoL (PLANNED)
- Channel group/category filtering (only import selected groups)
- Channel number offset / override
- Duplicate channel detection
- Import only favourites toggle

---

## Which Emby install is which
There are two Emby installs on this machine and they are **not** the same version:

| Path | Version | Role |
|------|---------|------|
| `%APPDATA%\Emby-Server\system` | 4.9.5.0 | **The server that actually runs and loads the plugin** |
| `C:\Program Files\Emby-Server\system` | 4.8.8.0 | Older leftover install — not running |

Data path of the running server is `%APPDATA%\Emby-Server\programdata`.

`Emby.FastIptv.csproj` auto-detects this: it references `%APPDATA%\Emby-Server\system` when present
and falls back to Program Files. The build prints which one it picked. Override with:

```
dotnet build Emby.FastIptv/Emby.FastIptv.csproj -c Release -p:EmbySystemDir="<path>\Emby-Server\system"
```

Building against the wrong version is silent at compile time and only shows up as the plugin
failing to load, so check the printed path if anything behaves oddly.

## Deployment checklist (after every build)
1. Copy `bin/Release/net8.0/Emby.FastIptv.dll` → `%APPDATA%\Emby-Server\programdata\plugins\Emby.FastIptv.dll`
   — a **flat DLL directly in `plugins\`**, not in an `Emby.FastIptv\` subfolder
2. Restart Emby Server
3. Hard-refresh browser (`Ctrl+Shift+F5`) to clear cached HTML/JS
4. Confirm the load in `%APPDATA%\Emby-Server\programdata\logs\embyserver.txt`:
   `Loading Emby.FastIptv, Version=<x.y.z.w>` — verify it is the version you just built

## Diagnosing playback failures
Emby turns the advertised video codec into an ffmpeg **input decoder override**, placed before `-i`:

```
-f mpegts -c:v:0 h264 -noautorotate -i "http://..."
```

If that forced decoder does not match the stream's real MPEG-TS `stream_type`, ffmpeg cannot parse
the elementary stream and playback dies before a single segment is written — even on a pure stream
copy, because the segment muxer needs dimensions:

```
[h264] no frame!
Could not find codec parameters for stream 0 ...: unspecified size
[segment] dimensions not set
Could not write header for output file #0 (incorrect codec parameters ?): Invalid argument
```

The real codec is the hex tag in ffmpeg's own stream line — `0x001B` = H.264, `0x0024` = HEVC:

```
Stream #0:0[0x100]: Video: h264 ([36][0][0][0] / 0x0024), none    <- forced h264 onto HEVC, broken
Stream #0:0[0x100]: Video: h264 (High) ([27][0][0][0] / 0x001B)   <- genuinely H.264, fine
```

This is why the plugin must not advertise guessed codecs. See `AdvertiseStreamMetadata`.
Per-playback logs: `%APPDATA%\Emby-Server\programdata\logs\ffmpeg-{remux,directstream,transcode}-*.txt`

### Where the start-up delay actually is
Measured on the running 4.9.5 server, `embyserver.txt` response times:

| Request | Fast probe (v1.0.3.1) | Fixed codecs (before) |
|---------|----------------------|----------------------|
| `PlaybackInfo` (the plugin's media-source path, probe included) | 1 ms × 50, 2 ms × 1, 1396 ms × 1 | 1 ms × 8, 2 ms × 2, 1439 ms × 1 |
| `live.m3u8` (Emby starts ffmpeg, waits for segment 0) | 250–390 ms × 7, 556–640 ms × 25, 878–1380 ms × 5 | 250–390 ms × 6, 888–1377 ms × 5 |

So the probe is not in the critical path once a channel is cached — `PlaybackInfo` answers in 1 ms.
The one ~1.4 s outlier is the first tune after a restart (M3U fetch) and predates the probe.

The visible spinner is Emby's `Waiting for 1 segments`. On a **stream copy** the segment muxer can
only start at a key frame **in the source**, and these channels have long GOPs (no IDR within ~1.9 s
of video), so that wait is 0.5–1.4 s. A **transcode** emits its own key frames and starts in
~250–390 ms, which is why the fast samples on both sides are the nvenc sessions. Nothing in the
plugin can shorten it — do not go looking for it in the probe.

The remaining plugin-side cost is one probe per channel per `ProbeCacheHours`, ~0.5 s, on the first
tune only. It could be moved off the critical path by probing in the background after a channel
refresh, at the price of one provider connection per channel — only sensible for short lists.

## Known gotchas
- Bump `AssemblyVersion` + `AssemblyFileVersion` in `AssemblyInfo.cs` on every release so Emby's ETag cache-busting works
- `XmlSerializer` cannot serialise `Dictionary<K,V>` — always use arrays for config collections
- `ReadAsStringAsync()` in .NET 8 accepts a `CancellationToken`; in older frameworks it does not
- `DateTimeOffset.ToUnixTimeSeconds()` is available in .NET 4.6+ / .NET Core
- `MediaSourceInfo.RequiresOpening` must be `true`, or recording is impossible: `EmbyTV` only gets
  an `ILiveStream` when the source requires opening, and `RecordingRequiresEncoding` /
  `DirectRecorder.Record` read `liveStream.SupportsCopyTo` with no null check
  (NullReferenceException; identical code in 4.9.5 and 4.10.1). `GetChannelStream` returns a
  `PassthroughLiveStream`: `Open` is a no-op, `SupportsCopyTo` is false, and `Path` stays the
  provider URL, so ffmpeg and the recorder fetch it themselves (direct HTTP copy for TS, ffmpeg for
  HLS) and the plugin's probed `MediaStreams` are kept
- Do **not** return Emby's own stream (`ILiveTvManager.CreateLiveStream` →
  `SharedHttpPipelineSource`): its `Open` waits 3.5–8 s for a buffer and then always runs ffprobe,
  which is exactly the built-in M3U tuner's slow start
- Per-tuner User-Agent and custom headers reach ffmpeg and the recorder only through
  `RequiredHttpHeaders` on the media source
- Each `PassthroughLiveStream` gets a unique `MediaSource.Id` (Emby derives `LiveStreamId` from it);
  streams are not shared, so a shared id would let one viewer's close hit another's stream
- `MediaSourceInfo.SupportsProbing` / `AnalyzeDurationMs` / `ReadAtNativeFramerate` / `BufferMs` are
  `[Obsolete]` and `SupportsProbing` is not referenced by `Emby.LiveTV.dll` at all — setting them
  achieves nothing; Emby decides probing itself
- `ILiveStream` differs by version: 4.9.x has `int ConsumerCount { get; set; }`; 4.10 drops the setter
  and adds `AddConsumer`/`RemoveConsumer`. The plugin implements all of them as **`virtual`** — the
  CLR maps only virtual methods onto interface members, and a member missing from the compile-time
  interface is otherwise emitted non-virtual and fails with TypeLoadException on the other version.
  Verify by loading the built DLL against real 4.9.5.0 and 4.10.1.0 server assemblies, not just by compiling
- `MediaStream.IsTextSubtitleStream` is read-only on 4.9.5 — assigning it does not compile
- The probe parsers under `LiveTv/Probe/` deliberately reference no MediaBrowser type, so they can be
  compiled into a plain console harness and diffed against `ffprobe` without an Emby install
- `ffprobe` reports H.264 `level` as the raw `level_idc` (42 = 4.2) but HEVC as `general_level_idc`
  (93 = 3.1, 150 = 5.0). `VideoParameterSets` mirrors that, so values match what Emby would have
  probed itself
- The `dvbsub` encoder in the bundled 4.9.5 ffmpeg segfaults, so DVB subtitle PMT entries could not
  be exercised end to end — that classification path is unverified against real content
