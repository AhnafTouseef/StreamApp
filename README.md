# StreamApp

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
| FFmpeg | installed together with FFprobe (the winget/apt/brew packages include both). StreamApp finds it automatically; it is what makes audio-track switching and English-first start work in Chrome/Edge/Firefox, and it plays MKV/HEVC/AC3 files (see [Player](#player)) |
| Network | the machine running StreamApp must reach the ISP servers; viewers' browsers must too for direct playback |

## 2. Setup

```bash
cd StreamApp

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

The SQLite file (`streamapp.db`, see `ConnectionStrings:Default`) is created on first start. Because the sandbox could not
run `dotnet ef`, **no migrations are shipped**: with no migrations present the app creates the schema with `EnsureCreated`.
To switch to proper migrations (recommended before you change the model):

```bash
dotnet tool install --global dotnet-ef
rm streamapp.db streamapp.db-shm streamapp.db-wal      # the EnsureCreated schema has no migration history
dotnet ef migrations add InitialCreate
dotnet run                                          # applies migrations at startup
```

On startup the app also marks scans that were still `Running` (killed process) as `Interrupted` so they resume on the next visit,
seeds the default categories (English, Hindi, Animation, South Indian → Hindi Dubbed / South Movies) and the sources from `Seed:Sources`.
**It never seeds movies** — until a scan finds some, pages show empty states.

## 3. How it works

### Categories and navigation
Navigation is read from the database (Home, your categories with sub-categories, Genres, Search). Add or reorder categories in
**Admin → Library**; they appear in the menu and at `/movies/{slug}` immediately, no code change. Deactivating a category hides it.

### Sources and the crawler
A *source* = one directory URL + a category + a scan strategy + a cooldown + optional include/exclude folder lists (admin panel → Library).

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
**Subtitles inside the video file** (MKV/MP4 text tracks: SubRip, ASS/SSA, mov_text, WebVTT) load by themselves: when the player opens, the server lets ffmpeg read the file once, converts every text track to WebVTT and hands them to your browser, which **keeps them in its own storage (IndexedDB)**. The CC menu shows them as "English (in video)". **They appear within a few seconds, like in a desktop player:** the server first jumps straight to the part of the file around your playhead and returns just those lines (a 10-minute window; jump somewhere else and the next window is fetched), while the complete extraction runs in the background. When it is done, the full subtitles are saved in your browser and every later visit loads them instantly without asking the server at all. The browser copy is asked to be kept permanently (`navigator.storage.persist()`) and is capped at the 300 most recently opened movies; if the browser still clears it, the next visit simply reads the subtitles again. The server never accumulates subtitle files: extracted files wait in the OS temp folder (`Media:SubtitleStagingDirectory`) for 15 minutes (`Media:SubtitleStagingMinutes`) and are then deleted. Subtitles switch on by themselves when the movie will not play with English audio, or when you left them on for the previous movie; otherwise they are listed and off. Needs ffmpeg. Image-based tracks (Blu-ray PGS, DVD) cannot be converted to text, so the menu just says so.
**What compatibility mode is doing:** the line under the player says whether the picture is copied (cheap: a few percent of one core) or re-encoded (heavy: most of the CPU), and why (the video is HEVC/AV1/VP9, it is 10-bit and the browser could not show the copy, a seek needed an exact start, or `Media:AlwaysEncodeVideo` is on). Audio is always converted to AAC, which is cheap.

**HEVC in compatibility mode:** when the browser says it can show HEVC (`canPlayType`, the same thing that lets direct play work), the server copies the HEVC picture into the converted stream (tagged `hvc1`) and only converts the audio, exactly like it does for H.264. That keeps the CPU low for HEVC movies. If the picture still does not show (for example an unsupported 10-bit profile) or the browser reports a decode error, the player falls back to re-encoding by itself. Browsers that cannot show HEVC still get the re-encoded picture. The line under the player says which of the two is happening.

**Compatibility mode switch:** under the player there is a labelled switch, "Compatibility mode: On/Off", and the control bar has a matching "Compat: on/off" button. Both do the same thing and stay in step. Your choice is remembered on that device. The switch is greyed out when the server has no ffmpeg, and it also turns on by itself when a file cannot be played directly.

**Sound and picture stay together in compatibility mode:** some files have audio that starts a few seconds after the picture (the browser would play the sound too early), and a seek can only start the copied picture on a keyframe. The server fills such a gap with silence. After a seek the player first asks the server where a copied picture would really start (`/media/remux/{id}/start?t=`, found with the very same ffmpeg seek the stream uses) and starts the sound at exactly that point (`k=` in the stream URL). When a copy cannot be exact (the nearest keyframe is more than 20 s before the point you picked, or it is an "open GOP" keyframe that browsers cannot start playing from, which is common in anime encodes) that one stream is re-encoded instead, which starts exactly where you seeked. Otherwise, playback can begin a few seconds before the point you picked, as in other players that copy the picture.

**If sound and picture are still out of sync in compatibility mode:** the server logs the exact ffmpeg command it used (`Compatibility stream for file …: ffmpeg …` in the console). Run it yourself with an output file instead of `pipe:1` and play that file in a desktop player. If it is in sync there, the problem is in how the browser handles the stream. To check whether copying the picture is the cause, set `Media:AlwaysEncodeVideo` to `true` (environment variable `Media__AlwaysEncodeVideo=true`): the picture is then always re-encoded (more CPU) and nothing depends on the file's keyframes.

**Phones and tunnels:** full screen turns the screen to landscape where the browser allows it (Android Chrome; iOS Safari does not allow it, but its own video full screen rotates by itself). On an https address (for example a Cloudflare tunnel) the browser may not load the ISP's plain-http file, so the player goes straight through the server relay. If a phone plays the sound but cannot decode the picture (HEVC / 10-bit files), the player notices after about 2 seconds and switches to compatibility mode, re-encoding the picture as plain 8-bit H.264 if needed (needs ffmpeg on the server; this uses server CPU).
**Drag and drop** is only the fallback for movies with no usable subtitles (or other languages): any subtitle file onto the video, or *Load subtitle file…* — encodings UTF-8/UTF-16/Windows-1252 are handled, HTML/ASS markup is never executed.
**Timing & size** panel (CC menu, or the *Adjust timing* button after a drop): ±0.1 s / ±1 s steps and a ±60 s slider (keys `G` earlier / `H` later), **Sync** (pause when you hear a line start; the closest
subtitle line is aligned to that moment), frame-rate fix presets (23.976 / 24 / 25 fps) for subtitles that drift, and size. Timing is remembered per movie and subtitle file in your browser.
The background extraction reads the whole video once from the ISP server (one at a time; `Media:MaxSubtitleExtractions`, timeout `Media:SubtitleExtractTimeoutMinutes`). Clearing the site data of your browser simply makes the next visit extract again.

*Autoplay and gestures:* the movie starts by itself when the page opens (if the browser blocks autoplay with sound it starts muted and shows an **Unmute** button).
On the picture: **tap/click** = play/pause · **double tap/click** on the left or right side = back/forward 10 s (keep tapping to keep skipping; quick taps are added up so a converted stream restarts only once) ·
**double tap in the middle** = fullscreen · **press and hold** = 2× speed while held (your chosen speed is restored on release).

*Speed:* a continuous slider from 0.25× to 4× (logarithmic, so 1× sits in the middle) that snaps to round values (0.25, 0.5, 0.75, 1, 1.25 … 4); keys `<` and `>` step through the round values.
Seek, volume, fullscreen and keyboard shortcuts (space/K, ←/→, ↑/↓, F, M) are included.

### Scan window for sources without year folders
For a source whose root holds the movie folders directly (strategy *Flat*), a routine scan only looks at folders modified within the last `Crawler:RecentWindowMonths` months (default 24; `0` switches it off). Older folders are skipped without being opened.
The **first scan** of a source and every **Deep scan** ignore the window, so the back catalog is still imported and a Deep scan picks up anything old that was missed (for example a folder moved in with an old date).

### Posters
Cards and detail pages use a fixed 2:3 frame, so an oddly sized poster image never makes a card bigger. The poster is the **TMDB poster when the movie is matched**; the poster file inside the ISP folder is the fallback
(`Crawler:PreferFolderPoster`, default `false`; set `true` to always prefer the folder poster). Movies imported earlier with the folder poster can be switched with one click on **Admin → Dashboard → Use TMDB posters** (runs in the background; the dashboard shows progress).

### Search

Search first looks for the text as typed (title, original title, genre). When nothing contains it, the site falls back to **similar titles**: typos (`intersteller`), joined or split words (`spiderman`, `starwars`, `back to future`), missing or reordered words (`lord rings`), accents and punctuation (`amelie`, `wall e`). The results are ordered by best match and a line above them says what was matched ("Did you mean …?"). Category, genre and year filters still apply. Very short words are never typo-matched, and unrelated text shows the normal "No results" page.

The similar-title search uses an in-memory index of every movie's title and original title. Nothing has to be started: it is built in the background when the app starts, and it follows the catalog by itself. After a scan adds or changes movies, after an admin edit, or after entries are removed, the next search notices (it re-checks the movie table at most every two seconds) and rebuilds it, which takes milliseconds for thousands of movies. Nothing is stored in the database, so there is no migration and no rebuild button.

The search box in the header shows matching titles as you type (from two letters on): titles that start with what you typed come first, then titles where each typed word starts a word of the title, then titles that contain it, then similar titles for typos. It uses the same index through `GET /api/suggest?q=`, hides entries in hidden categories like the rest of the site, and supports the keyboard (↑ ↓ Enter, Esc) and touch. Enter with nothing highlighted runs the normal search.

### Browse lists
Category, genre and search lists default to **Most popular** (TMDB popularity; unmatched entries fall to the end). Pick *Recently added*, *Release year*, *Rating* or *Alphabetical* from the Sort dropdown.

### Home page
Hero (most popular titles with a backdrop) · *Trending Now* (popular recent releases) · *Popular Movies* · *Top Rated* · *Recommended for You* · then **one slider per category** ("Popular in English Movies", …)
with that category's most popular recent movies (sub-categories included) and a *See all* link. Sliders have arrows, drag/swipe, snap scrolling and page dots.
"Recently added" is no longer on the home page (it is still a sort option on every category page). Popularity comes from TMDB; without TMDB data the home page shows *New Releases* and *New in …* sliders instead.

### Admin panel (`/admin`)
**Stopping a scan:** while a source is scanning, *Scan now* turns into **Stop scan** (Library and Scans pages). The scan ends within moments and is recorded as *Cancelled*; movies found so far stay, and a category visit will not restart it right away. Press *Scan now* to continue.
Dashboard (counts, running scans, recent failures) · **Library** (categories and their sources together on one page: each category lists its sources; add/edit happens in popup forms; **Test connection** with strategy suggestion, enable/disable, delete) ·
Scans (history, start, deep scan, retry) · Movies (filter by source and category, search, edit fields, TMDB re-match, re-inspect streams, probe the ISP server, remove from catalog). **Edit opens in a popup** over the list, so your filters and page stay as they are; closing it refreshes the list in place. Ctrl/Shift-click an Edit link to get the full page instead.
Cookie authentication, antiforgery on every form, PBKDF2 password hashing, 5 failed logins → 5-minute lockout per client.
Removing a movie only deletes the catalog row — nothing is touched on the ISP server.

## 4. Settings reference

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=streamapp.db` | SQLite file |
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
| `Media:ProbeTimeoutSeconds`, `MaxConcurrentProbes` | 90, 2 | limits |
| `Media:MaxRemuxSessions` | 0 | most compatibility streams that only copy the picture (0 = no limit; they cost a few percent of a core each) |
| `Media:MaxEncodeSessions` | 0 | most streams that re-encode the picture at once (0 = automatic: half the CPU threads, at least 1 — or, when a hardware encoder is in use, the CPU thread count, at least 2; -1 = no limit). A stream waits a few seconds for a free slot, then gets a 503 "busy" instead of silently failing |
| `Media:CopyAudio` | true | allow copying AAC/MP3/AC3/E-AC3 sound in compatibility mode (the Fastest and Slow speeds) when its timing lines up; false = always convert the sound |
| `Media:HardwareEncoder` | auto | where a full picture re-encode runs: `auto` tries NVIDIA NVENC, Intel Quick Sync, AMD AMF (Linux: VAAPI) with a real half-second test encode and uses the first that works; `none` = always the CPU (libx264); `nvenc`, `qsv`, `amf`, `vaapi` = only that one; `custom` = use `Media:HardwareEncoderArgs` |
| `Media:HardwareEncoderArgs` | empty | ffmpeg video arguments for `custom`, e.g. `-c:v h264_qsv -preset veryfast -global_quality 24 -pix_fmt nv12` |
| `Site:Directory` | site-data | folder for the site settings and the uploaded logo/favicon (Admin > Site) |
| `Site:Name` | empty | site name used until one is saved in Admin > Site |
| `Media:HardwareDecode` | true | also let the graphics hardware decode the source (`-hwaccel auto`); this is the half that matters most for HEVC |
| `Admin:Username`, `PasswordHash`, `Password`, `SessionHours` | admin, –, –, 8 | admin login |
| `Seed:Sources[]` | the two ISP servers | sources created on first start (name, category slug, URL, strategy, cooldown, include/exclude) |

## Hiding a category

**Hide** on Admin > Library removes the category from the navigation and hides its movies everywhere: home rows, search, genre lists, the category page and direct
movie/watch links (these return 404 for visitors; a signed-in admin can still preview). Sub-categories of a hidden category are hidden with it. Nothing is deleted; **Show** brings it all back.
Direct media URLs for a hidden movie are not separately blocked; the pages that hand out those URLs are.

## Removing entries (debugging a bad scan)

Nothing here ever touches the ISP server; only StreamApp's own database changes.

- **Admin > Library > source menu > Clear movies**: removes every movie entry of that source and forgets what was scanned, but keeps the source. Press *Scan now* to index it again from scratch.
- **Admin > Library > source menu > Delete**: removes the source *and* all of its movie entries (the confirm dialog shows how many).
- **Admin > Movies**: tick entries and press *Delete selected*. With a filter active (source, match status or search text) a *Delete all N matching the filter* button removes every page of results at once. It is hidden without a filter, so the whole catalog cannot be wiped by one click.

## Changing a source's category

A movie copies its category from its source when it is first indexed. Saving a source with a different category now moves its movies too
(only those still in the old category; a movie you filed elsewhere by hand stays put). For entries that were misfiled earlier, the Library page
shows "N of its movies are filed under a different category" with a **Move them to ...** button.

## FFmpeg inside the project

ffmpeg and ffprobe are looked up in this order, and both are looked up the same way, so they can no longer end up in different places:

1. `Media:FfmpegPath` / `Media:FfprobePath`, only if you set them (leave them empty)
2. the project's **`tools`** folder (`StreamApp\tools`)
3. PATH, then the usual install folders (winget, Chocolatey, Scoop, Homebrew)

To set up: start the site, sign in, open **Admin > Dashboard** and press **Download FFmpeg into the project** (Windows). Or copy `ffmpeg.exe` and
`ffprobe.exe` into `StreamApp\tools` yourself and press **Check again** (no restart needed). To move to a new computer, copy the whole project folder;
the tools go with it. The binaries are git-ignored, so they are not committed.

## Stream method: how much the server converts

With compatibility mode on, the player shows four stream methods under the switch. Viewers only see how fast each one streams and seeks, never what the server does for it, so the buttons are named by speed, not by technique:

| Stream method | Picture | Sound | When to try it |
|---|---|---|---|
| **Fastest** (default) | copied | copied | almost every movie |
| **Fast** | copied | converted to AAC | the sound is missing or does not play |
| **Slow** | re-encoded | copied | the picture does not show or stutters with a copy |
| **Slowest** | re-encoded | converted | last resort, most compatible |

A hint next to the buttons says to start with the fastest and step down only if the picture or sound is missing, choppy or out of step, and that slower methods stream and seek more slowly but work with more files. (It is a "hint" for the *Hide hints and tips* setting, and the buttons go with *Hide the compatibility-mode switch and button*, see Admin > Site.)

- **Only what can work is offered.** A speed that cannot work for this movie is greyed out: no copy of a picture this browser cannot show (HEVC without browser support, for example), no copy of sound this browser cannot play inside an MP4. The browser itself is asked, so AC3 is offered only where it can decode it.
- **Sound that is copied:** AAC, MP3, AC3 and E-AC3 (MKV and MP4 sources). DTS, TrueHD, PCM, MP2, Opus, Vorbis and FLAC are always converted.
- **Timing is checked first.** A browser ignores a late start of one track in this kind of MP4, so before copying the server measures where the picture and the sound start. If they are more than 0.1 s apart, or the picture is re-encoded after a jump, the sound is converted instead (with its timing fixed). The log says which and why (`picture copied, sound converted (the sound starts +2.99 s from the picture)`).
- **If a copied sound gives nothing to play** (Chrome, Edge and Safari report how much sound they decoded), the player steps down by itself to the next speed, but only after the movie has really played for a moment, so a slow connection is never mistaken for it (see below). The step lasts for that page view only: the speed saved for a movie is always what you chose, and the next visit tries the cheaper speed again, so the failure stays visible and can be looked into. The browser console shows what the player saw (`[stream] ...`: decoded sound, picture frames, seconds played, the browser's error text). Nothing is closed by an automatic step: every method that is possible for the movie stays selectable in the settings. A method you choose (now or on an earlier visit) is never changed by the player: if it does not work, it says so in plain words and leaves it, so you can look into it. Only the default (Fastest) is moved off by the player. What the browser claims to play (`canPlayType`, for HEVC and AC3 / E-AC3) only advises the default method; it never greys out a button. Every method the server can do for the movie is selectable (the server copies H.264 and HEVC pictures, and AAC, MP3, AC3 and E-AC3 sound; the admin's "always re-encode" setting still applies), and a button the browser doubts says so in its tooltip.
- `Media:CopyAudio` (default true) switches sound copying off for the whole server: the sound is then always converted, as before.

### Startup and fallback: evidence, not timers

A slow or interrupted connection looks like a codec the browser cannot play (nothing shows, nothing sounds), so the player never judges a codec by how long something took.

- **Decoding is judged by what the browser did with the data.** After 1.5 s of the movie has really played (which proves the data arrived), it reads the decoder counters: sound decoded nothing while the picture decodes means the copied sound is the problem; sound decodes but no picture frame does means the picture is. The picture is also judged as soon as the file's header is read and has no usable picture track. Only the failed part changes (sound converted, or picture re-encoded); the other keeps its method. Nothing is remembered: an automatic step lasts for the page view only.
- **Delivery trouble never changes the method.** A network error, a connection that stops delivering, a decode error after sound and picture both decoded fine (data cut off or damaged), a busy server (503) and a lost connection are all answered by asking for the same stream again at the same position, after a pause that doubles up to 15 s. Nothing is remembered. Each reconnect of a converted stream uses a slightly different address (`r=`), because Chrome can answer a repeat of a failed address from what it kept of the failure. A browser that is offline waits for it to come back. After six tries in a row that did not get a second of the movie played, the player says the connection was lost (Retry keeps the method and the position).
- **How long silence is tolerated adapts.** A connection that keeps delivering, however slowly, is never replaced; the player just says it is waiting for data. One that delivers nothing is replaced after at least 10 s while playing, or 45 s before the first data of a converted stream (the server may be looking up the start point, waiting for a free converter slot and starting ffmpeg; each reconnect is given longer), or three times the longest quiet spell it has survived, up to 60 s (90 s before the first data). This only decides how soon a dead connection is replaced, never the stream method.
- **When the browser cannot open a stream at all** it reports a busy server and an unplayable file the same way, so the player asks the source directly. Busy or unreachable: same stream again. For a converted stream, Chrome words a failed request differently from data it received and could not use, so a failed request is never blamed on the format; an empty answer gets about fifteen seconds of spaced tries first. Only repeated failures that did reach the decoder step down, one part at a time, sound first.
- **The server keeps one converted stream per viewer and movie.** A reconnect, seek or method change from the same page (the `s` parameter, a random id per page view) stops that page's previous stream and its ffmpeg at once, instead of leaving it until the operating system notices a dead connection. Streams without the parameter behave as before. Slot limits and busy answers are unchanged.

## Site: name, logo, colours and what visitors see

Admin > **Site** changes how the site looks and what it says, without editing any file. Everything applies to every page as soon as you save.

- **Hide debugging details and hints** (one button). Hides the playback status line, codec and server details in error messages, notes that mention ffmpeg or the dashboard, the source-folder line on a movie page, and the how-to texts and tips (subtitle loading help, timing help, "close matches" notes, menu explanations). Fine tuning: *Hide debugging details* and *Hide hints and tips* can be set separately, *Hide the compatibility-mode switch and button* is its own option (playback still switches to compatibility mode by itself when a device needs it), and *Still show everything to me when I am signed in* lets you keep seeing it all on your own devices.
- **Name and logo.** Site name (header, browser tab, admin area), tagline (search-engine description), logo upload (PNG, JPG, GIF, WebP or SVG, up to 2 MB), logo height, header display (logo and name, logo only, name only), the symbol used when there is no logo, and a separate favicon (PNG, SVG or ICO, up to 512 KB). Without an uploaded favicon the logo is used, and without a logo a small icon is drawn from the accent colour and symbol.
- **Colours.** Accent, lighter accent, page background, panels and text, with a live preview. The other shades (borders, secondary text) are worked out from these. The theme is designed for dark backgrounds.
- **Wording.** The label above the featured movie, the search box placeholder, an announcement bar shown on every page, the footer text, and switches for the "Metadata by TMDB" credit (TMDB asks sites using its data to credit it) and the footer Admin link (open `/admin` yourself if you hide it).
- **Custom CSS** for anything else. It is added after the normal styles. If a rule breaks the layout, empty the box and save.
- **Reset everything to default** returns to the original look and removes the uploaded images.

Where it is stored: `site.json` and the uploaded images live in the folder `Site:Directory` (default `site-data`, inside the project, next to the database). The database is not touched, so updating the app never needs a migration. Back up or move that folder together with `streamapp.db`. `Site:Name` in `appsettings.json` only sets the name used until one is saved in the admin panel.

Uploaded images are checked by their content, not their name; an SVG that contains scripts or event handlers is refused, and images are served with a header that stops them running anything.

## Hardware re-encoding

Copying the picture (the usual compatibility-mode case) needs no encoder at all. When the picture *must* be re-encoded (10-bit video, HEVC the browser cannot show, a jump that a copied picture cannot start exactly at), the server uses the graphics hardware if it can, so the CPU stays free for other viewers.

- **Detection:** once after startup (and again if the admin installs a different FFmpeg) the server runs a real test encode with each candidate encoder. An encoder that FFmpeg lists but the machine cannot run is skipped. The result is in the log (`Hardware video encoder: Intel Quick Sync…` or `No hardware video encoder found…`).
- **Fallback:** if a stream produces no video on the hardware encoder, the same request is started again with the software encoder; the viewer is not shown an error. The hardware encoder is then left alone for 10 minutes. If the software encoder also produces nothing (the source server refused, for example) the hardware encoder is *not* blamed.
- **Budget:** hardware re-encodes use their own, larger slot budget (see `Media:MaxEncodeSessions`).
- **Status line:** the player says when the re-encode runs on the graphics hardware.
- **What it cannot promise:** picture quality at the same CRF differs slightly between encoders, and the exact speed depends on the GPU and its driver. Quick Sync on an Intel UHD 620 laptop supports H.264 encoding and HEVC decoding; the first time you use it, check the log line above and try an HEVC file with compatibility mode on.

## 5. Deployment notes
- Put it behind a reverse proxy with HTTPS if exposed beyond your LAN. The streaming proxy is not rate-limited.
- Mixed content: an `https://` StreamApp cannot play `http://` ISP URLs directly; the player automatically falls back to `/media/stream/{id}`
  (which then carries the traffic through this server).
- Back up `streamapp.db` — it holds categories, sources, manual metadata corrections and scan history. The catalog itself can be rebuilt with a deep scan.

## Known limitations
- **EF Core / SQLite were not built or run in the authoring sandbox.** Expect the first build to be the first real compile of the data layer.
  Queries were written to be SQLite-translatable (no client-side evaluation tricks, `LIKE` with escaping, simple projections), but that is unverified —
  if a page throws an "could not be translated" error, the log names the query.
- The ISP servers were not reachable from the sandbox. The h5ai/nginx parser was tested against realistic h5ai-style fallback tables and nginx autoindex output,
  not against your actual servers. Use **Admin → Library → source menu → Test connection** first and read the suggested strategy; the second server (`DHAKA-FLIX-7/English Movies/`)
  is configured as `Auto` precisely because its layout is unknown — inspect it before trusting the guess.
- The initial import of a year-based source walks every year (see above) and can take hours on a large server.
- Remux seeking restarts ffmpeg at the requested time; with copied video it snaps to the previous keyframe, or the stream is re-encoded when that keyframe cannot be copied cleanly (a seek then costs more CPU and takes a moment longer).
- Image-based embedded subtitles (Blu-ray PGS, DVD VobSub) cannot be shown; text-based embedded and external subtitle files are playable.
- Browsers can't decode every codec (HEVC/10-bit, AC3/DTS audio, some MKVs). Without ffmpeg configured those files show the error state instead of failing silently.
- In-memory login throttling resets on restart; the scan gate is per process (single instance only).
