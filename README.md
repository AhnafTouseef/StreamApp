# MovieHub

A Netflix-style movie discovery and streaming site for movies that already live on your ISP's directory servers
(nginx / h5ai listings such as `http://172.16.50.7/DHAKA-FLIX-14/` and `.../DHAKA-FLIX-7/`).

ASP.NET Core 8 Razor Pages, EF Core 8 + SQLite, TMDB for metadata, FFprobe for stream inspection.
Movie files are **never downloaded** — the browser plays them straight from the ISP server
(or through a range-aware pass-through proxy when the browser can't reach the server directly).

> **Read this first — verification status.** The sandbox this was written in could not download NuGet packages,
> so the real EF Core / SQLite packages were never compiled or run. Everything else (all application code, Razor views,
> crawler, player endpoints) was compiled and tested against an in-memory stand-in for EF Core and against a fake
> h5ai/TMDB server (160 unit checks, 36 endpoint checks, end-to-end scan/admin scenarios). The first `dotnet build`
> on your machine is therefore the first build against real EF. See [Known limitations](#known-limitations).

## 1. Requirements

| | |
|---|---|
| .NET 8 SDK | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| TMDB API key | free at <https://www.themoviedb.org/settings/api> (v3 key or v4 read-access token) |
| FFprobe | part of FFmpeg (`apt install ffmpeg`, `brew install ffmpeg`, or <https://ffmpeg.org>) — needed for audio/subtitle/resolution info |
| FFmpeg | installed together with FFprobe (the winget/apt/brew packages include both). MovieHub finds it automatically; it is what makes audio-track switching and English-first start work in Chrome/Edge/Firefox, and it plays MKV/HEVC/AC3 files (see [Player](#player)) |
| Network | the machine running MovieHub must reach the ISP servers; viewers' browsers must too for direct playback |

## 2. Setup

```bash
cd MovieHub

# 1. TMDB key (development: user-secrets; production: environment variable or appsettings.Production.json)
dotnet user-secrets set "Tmdb:ApiKey" "<your key or token>"
#   or:  export Tmdb__ApiKey="<your key or token>"

# 2. Admin password (store the HASH, not the password)
dotnet run -- hash-password "choose a strong password"
dotnet user-secrets set "Admin:PasswordHash" "<output of the previous command>"
#   or:  export Admin__PasswordHash="v1...."
#   For a throw-away local test only you may set Admin:Password in plain text (a warning is logged).

# 3. FFmpeg / FFprobe: nothing to configure. They live INSIDE the project, in the tools folder (see "FFmpeg inside the project" below).

# 4. Run
dotnet run
```

`dotnet run` uses `Properties/launchSettings.json`, which sets `ASPNETCORE_ENVIRONMENT=Development` — that is what makes user-secrets load
and shows detailed error pages. Outside `dotnet run` (a published build, a service) the environment is Production: user-secrets are **not** read there,
so use environment variables or `appsettings.Production.json` instead.

Open <http://localhost:5000>. To reach it from other devices on your LAN, change `applicationUrl` in `launchSettings.json` to `http://0.0.0.0:5000`
(or set `ASPNETCORE_URLS`) and allow the Windows Firewall prompt.
The admin panel is at `/admin` (user `admin` unless you changed `Admin:Username`).

`appsettings.Example.json` shows every setting you normally override. Any setting can also be an environment variable
using `__` as the separator (`Crawler__DefaultCooldownHours=6`). **No secret is stored in the repository.**
If `Admin:PasswordHash` / `Admin:Password` are both empty, admin login is disabled.

### Database

The SQLite file (`moviehub.db`, see `ConnectionStrings:Default`) is created on first start. Because the sandbox could not
run `dotnet ef`, **no migrations are shipped**: with no migrations present the app creates the schema with `EnsureCreated`.
To switch to proper migrations (recommended before you change the model):

```bash
dotnet tool install --global dotnet-ef
rm moviehub.db moviehub.db-shm moviehub.db-wal      # the EnsureCreated schema has no migration history
dotnet ef migrations add InitialCreate
dotnet run                                          # applies migrations at startup
```

On startup the app also marks scans that were still `Running` (killed process) as `Interrupted` so they resume on the next visit,
seeds the default categories (English, Hindi, Animation, South Indian → Hindi Dubbed / South Movies) and the sources from `Seed:Sources`.
**It never seeds movies** — until a scan finds some, pages show empty states.

## 3. How it works

### Categories and navigation
Navigation is read from the database (Home, your categories with sub-categories, Genres, Search). Add or reorder categories in
**Admin → Categories**; they appear in the menu and at `/movies/{slug}` immediately, no code change. Deactivating a category hides it.

### Sources and the crawler
A *source* = one directory URL + a category + a scan strategy + a cooldown + optional include/exclude folder lists (admin panel → Sources).

| Strategy | Meaning |
|---|---|
| `Auto` | look at the root listing: ≥3 year-like folders → year-based, otherwise flat |
| `YearBased` | root contains `(2026) 1080p`, `2000 & Before`, `(1995) 1080p & Before`, … Each movie is a folder inside a year folder |
| `Flat` | each sub-folder of the root is one movie |
| `RecursiveDiscovery` | walk the tree; a folder that directly contains video files is a movie |
| `ManualFull` | like recursive, but never scanned automatically — only from the admin panel |

**When a scan runs:** *only* when somebody opens a category page whose source is past its cooldown (default 12 h), or when an admin starts
one. The visit that triggers it never waits for it — the page renders from the database while the scan runs in the background.
There is no continuous background crawler.

**What a routine scan lists (year-based sources):** the latest year folder, any year newer than the newest one already known
(plus the previous latest year once, because it may have received late additions), and folders you explicitly *included*
(e.g. `James Bond Collection`). Older years are skipped. **Deep scan** (Admin → Scans) re-lists every year.
**The very first scan of a source is an initial import and lists every year, newest first** — on a big server that can take a long time,
but it runs in the background and movies appear as they are committed.

**Safety rules (all enforced in code and covered by tests):**
- one scan per source at a time (atomic gate), a global cap of `Crawler:MaxConcurrentScans`;
- a failed scan (ISP down, timeouts) is recorded as `Failed`, **changes nothing** in the catalog, and backs off for `Crawler:FailureBackoffMinutes`;
  the admin panel has a Retry button;
- each movie is committed on its own, so an interrupted scan leaves only complete movies and resumes where it stopped;
- only registered, enabled sources are ever requested; every URL must be under a source root (same scheme/host/port/path prefix),
  must not contain credentials or `..`, and redirects are re-validated hop by hop. Link-local addresses (`169.254.x.x`, `fe80::`) cannot be registered;
- movie files are only *probed* (FFprobe reads container headers over HTTP range requests) — never downloaded.

**Duplicates are never merged.** Every source folder is its own catalog entry; there is no unique constraint on title or TMDB id.
If the same film exists on both servers you get two entries (and the details page lists "other copies").

### Metadata
Folder names are parsed (`Title (Year) 1080p [Dual Audio]`), then TMDB is searched with title + year.
A TMDB id is stored **only for a confident match** (exact normalised title, year within 1, no competing candidate). Anything else is marked
*Ambiguous* or *Unmatched*, kept in the catalog with the parsed title, and can be fixed in **Admin → Movies → Edit** (search TMDB, pick the right
one, or enter an id; "Clear match" reverts). Unmatched entries are retried after TMDB outages. Posters found in the movie folder are
preferred over TMDB posters (`Crawler:PreferFolderPoster`). IMDb ratings come from the official OMDb API if you set `Omdb:ApiKey` — IMDb is never scraped.

### Player
`/watch/{id}` uses a custom player. Fallback chain:
1. play the ISP URL directly (best: no load on this server);
2. `/media/stream/{id}` — pass-through proxy with full `Range` support, streamed in 80 KB chunks, never buffered;
3. `/media/remux/{id}` — ffmpeg fragmented-MP4 pipe (needs ffmpeg, found at startup: `Media:FfmpegPath` if set, else the project's `tools` folder, then PATH);
4. a clear error message.

*Audio:* **a file with several audio tracks starts with the English one** (language tag English/eng/en, or a track title such as "ENG 5.1"; otherwise the file's own default).
Browsers other than Safari cannot switch tracks themselves, so when the English track is not the file's default the player starts from a server-side remux that carries it
(the remux begins when you press Play, so just opening the page uses no server resources). The headphone menu lists all tracks; picking another one restarts playback at the same position
with that track (video is copied when it is H.264, otherwise re-encoded; audio becomes stereo AAC). Safari switches natively. If ffmpeg is missing the menu lists the tracks read-only and says why.

*Subtitles:* drawn by the player itself, so they work after audio switches and seeks. **CC menu → pick a file, or Off** (key `C` toggles). External `.srt/.vtt/.ass/.ssa` files next to the movie are offered automatically.
**Drag and drop** any subtitle file onto the video (or use *Load subtitle file…*) — encodings UTF-8/UTF-16/Windows-1252 are handled, HTML/ASS markup is never executed.
**Timing & size** panel (CC menu, or the *Adjust timing* button after a drop): ±0.1 s / ±1 s steps and a ±60 s slider (keys `G` earlier / `H` later), **Sync** (pause when you hear a line start; the closest
subtitle line is aligned to that moment), frame-rate fix presets (23.976 / 24 / 25 fps) for subtitles that drift, and size. Timing is remembered per movie and subtitle file in your browser.
Subtitle tracks embedded inside the video container cannot be shown (extracting them would mean reading the whole movie file, which this project never does).

*Speed:* a continuous slider from 0.25× to 4× (logarithmic, so 1× sits in the middle) that snaps to round values (0.25, 0.5, 0.75, 1, 1.25 … 4); keys `<` and `>` step through the round values.
Seek, volume, fullscreen and keyboard shortcuts (space/K, ←/→, ↑/↓, F, M) are included.

### Home page
Hero (most popular titles with a backdrop) · *Trending Now* (popular recent releases) · *Popular Movies* · *Top Rated* · *Recommended for You* · then **one slider per category** ("Popular in English Movies", …)
with that category's most popular recent movies (sub-categories included) and a *See all* link. Sliders have arrows, drag/swipe, snap scrolling and page dots.
"Recently added" is no longer on the home page (it is still a sort option on every category page). Popularity comes from TMDB; without TMDB data the home page shows *New Releases* and *New in …* sliders instead.

### Admin panel (`/admin`)
Dashboard (counts, running scans, recent failures) · Categories · Sources (create, edit, **Test connection** with strategy suggestion, enable/disable, delete) ·
Scans (history, start, deep scan, retry) · Movies (search, edit fields, TMDB re-match, re-inspect streams, probe the ISP server, remove from catalog).
Cookie authentication, antiforgery on every form, PBKDF2 password hashing, 5 failed logins → 5-minute lockout per client.
Removing a movie only deletes the catalog row — nothing is touched on the ISP server.

## 4. Settings reference

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=moviehub.db` | SQLite file |
| `Tmdb:ApiKey` / `Language` / `MaxConcurrency` | – / `en-US` / 3 | TMDB access |
| `Omdb:ApiKey` | empty | optional IMDb ratings (official API) |
| `Crawler:DefaultCooldownHours` | 12 | default per-source cooldown (editable per source) |
| `Crawler:MaxConcurrentScans` | 2 | global scan cap |
| `Crawler:MaxConcurrentFolderTasks` | 3 | parallel folder inspections per scan |
| `Crawler:RequestTimeoutSeconds` | 30 | directory-listing timeout |
| `Crawler:MaxRecursionDepth` | 6 | for `RecursiveDiscovery` / `ManualFull` |
| `Crawler:FailureBackoffMinutes` | 60 | wait after a failed scan |
| `Crawler:PreferFolderPoster` | true | folder poster before TMDB poster |
| `Media:FfprobePath` / `FfmpegPath` | empty / empty | leave empty to use the project's tools folder, then PATH. A value is used exclusively (no fallback) |
| `Media:ToolsDirectory` | `tools` | folder (relative to the project) that holds ffmpeg and ffprobe |
| `Media:FfmpegDownloadUrl` | gyan.dev release-essentials zip | what the admin "Download FFmpeg" button fetches (https only) |
| `Media:ProbeTimeoutSeconds`, `MaxConcurrentProbes`, `MaxRemuxSessions` | 90, 2, 2 | limits |
| `Admin:Username`, `PasswordHash`, `Password`, `SessionHours` | admin, –, –, 8 | admin login |
| `Seed:Sources[]` | the two ISP servers | sources created on first start (name, category slug, URL, strategy, cooldown, include/exclude) |

## FFmpeg inside the project

ffmpeg and ffprobe are looked up in this order, and both are looked up the same way, so they can no longer end up in different places:

1. `Media:FfmpegPath` / `Media:FfprobePath`, only if you set them (leave them empty)
2. the project's **`tools`** folder (`MovieHub\tools`)
3. PATH, then the usual install folders (winget, Chocolatey, Scoop, Homebrew)

To set up: start the site, sign in, open **Admin > Dashboard** and press **Download FFmpeg into the project** (Windows). Or copy `ffmpeg.exe` and
`ffprobe.exe` into `MovieHub\tools` yourself and press **Check again** (no restart needed). To move to a new computer, copy the whole project folder;
the tools go with it. The binaries are git-ignored, so they are not committed.

## 5. Deployment notes
- Put it behind a reverse proxy with HTTPS if exposed beyond your LAN. The streaming proxy is not rate-limited.
- Mixed content: an `https://` MovieHub cannot play `http://` ISP URLs directly; the player automatically falls back to `/media/stream/{id}`
  (which then carries the traffic through this server).
- Back up `moviehub.db` — it holds categories, sources, manual metadata corrections and scan history. The catalog itself can be rebuilt with a deep scan.

## Known limitations
- **EF Core / SQLite were not built or run in the authoring sandbox.** Expect the first build to be the first real compile of the data layer.
  Queries were written to be SQLite-translatable (no client-side evaluation tricks, `LIKE` with escaping, simple projections), but that is unverified —
  if a page throws an "could not be translated" error, the log names the query.
- The ISP servers were not reachable from the sandbox. The h5ai/nginx parser was tested against realistic h5ai-style fallback tables and nginx autoindex output,
  not against your actual servers. Use **Admin → Sources → Test** first and read the suggested strategy; the second server (`DHAKA-FLIX-7/English Movies/`)
  is configured as `Auto` precisely because its layout is unknown — inspect it before trusting the guess.
- The initial import of a year-based source walks every year (see above) and can take hours on a large server.
- Remux seeking restarts ffmpeg at the requested time; with copied video it snaps to the previous keyframe.
- Subtitle tracks embedded inside a container cannot be shown by browsers; only external subtitle files are playable.
- Browsers can't decode every codec (HEVC/10-bit, AC3/DTS audio, some MKVs). Without ffmpeg configured those files show the error state instead of failing silently.
- In-memory login throttling resets on restart; the scan gate is per process (single instance only).
