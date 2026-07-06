# SynthDiscordRPC

Discord Rich Presence for **Synth Riders** (MelonLoader 0.7.2, .NET 6 / IL2CPP).

Shows on your Discord profile:

- **In a song** — song title, artist, difficulty, and a live elapsed timer
- **In the menus** — a configurable idle presence ("In the Menus / Browsing songs")

One DLL works on **both** game branches (Unity 2021.3.45f2 and Unity 6000.3.13). The mod
references no Unity or game assemblies — all game access is runtime reflection, and the
Discord side talks the local IPC protocol directly over a named pipe (no Discord Game SDK,
no native DLLs, no internet access, no account credentials).

---

## Installing (end users)

1. Install [MelonLoader](https://melonwiki.xyz) 0.7.2+ into Synth Riders (PCVR).
2. Drop `SynthDiscordRPC.dll` into `<game>/Mods/`.
3. Make sure the Discord desktop app is running on the same PC.

That's it — the mod ships with a built-in Discord Application ID, so no account setup
is needed. If Discord isn't running the mod idles harmlessly and connects when it appears.

## Maintainer setup (one time, before building a release)

The presence needs a Discord **Application ID** so Discord knows what name/art to show.
Application IDs are public information (no bot, no OAuth, no token), so it is safe to
embed one in the released DLL:

1. Go to <https://discord.com/developers/applications> and click **New Application**.
2. Name it `Synth Riders` — this exact name is what shows as "Playing Synth Riders".
3. On the **General Information** page, copy the **Application ID**.
4. Paste it into the `DefaultClientId` constant at the top of `Main.cs`.
5. *(Optional, for the game logo)*: under **Rich Presence → Art Assets**, upload an image
   and name the asset `logo` (or change `LargeImageKey` in the config to match).
   Modern Discord also accepts a full `https://` image URL directly in `LargeImageKey`.

Users can still override the ID via the `ClientId` config entry if they want their
own application.

## Config

| Entry | Default | Purpose |
|---|---|---|
| `ClientId` | *(empty)* | Optional override of the built-in Application ID. |
| `ShowSongDetails` | `true` | `false` = privacy mode, shows only "Playing a song". |
| `MenuDetailsText` | `In the Menus` | Top line while not in a song. |
| `MenuStateText` | `Browsing songs` | Second line while not in a song. |
| `LargeImageKey` | `logo` | Art asset key (or https URL). Empty = no image. |
| `LargeImageText` | `Synth Riders` | Hover text on the image. |
| `AlbumArt` | `true` | Show custom-song cover art from synthriderz.com as the presence image (logo moves to the small corner overlay). OST songs fall back to the logo. |
| `DebugLogging` | `false` | Verbose logs + one-time `Game_InfoProvider` and synthriderz-API member dumps. |

## Album art (custom songs)

Discord cannot display local images, and custom-song covers live inside local `.synth`
files — so the mod looks the song up on **synthriderz.com** (title + artist) and uses the
hosted cover URL as the presence image. Results (including misses) are cached in
`UserData/SynthDiscordRPC/covercache.json`, so each song costs at most one query ever.
OST songs aren't on synthriderz and cleanly fall back to the logo. Lookups run on a
background thread; the presence appears instantly with the logo and upgrades to cover
art about a second later.

The API search syntax and cover field name are probed from candidates; if covers stop
appearing after a site update, enable `DebugLogging` — the log prints the real JSON keys
of the first API item (`[CoverArt] beatmap item keys: ...`) to update the candidate list
from. You can inspect the API yourself with:
`curl "https://synthriderz.com/api/beatmaps?limit=1"`

## Behaviour notes

- **Discord not running / closed mid-session:** the mod silently retries every 5 seconds
  and re-sends the current presence when Discord comes back. No errors, no game impact.
- **Results screen:** the song presence remains until you return to the menu
  (`ReturnToMenu` is the end signal), which reads naturally on Discord.
- **Rate limiting:** presence writes use a sliding window of 4 per 20 seconds
  (Discord's limit is ~5 per 20s), so the song-start burst (logo presence + cover-art
  upgrade) goes out immediately; excess writes coalesce to the latest state.
- **Threading:** all pipe I/O lives on a background thread; Harmony callbacks on the
  main thread only flip a lock-guarded state slot. Zero frame-time cost.

## How it detects songs

Patch points (verified against live class dumps from prior probe sessions):

- `GameControlManager.Awake` — song scene loading
- `GameControlManager.OnLevelFinishedLoading` — song metadata (`Game_InfoProvider`)
  is populated by `SetSongStatusData()` around this point; read here with a short
  retry window
- `GameControlManager.ReturnToMenu` — song end

Song metadata is read from the `Game_InfoProvider` singleton using candidate member
name sets (`_name`/`_songName`/`_title`/…, `_author`/`_artist`/…) so branch naming
differences are tolerated. If a branch ever renames things, set `DebugLogging = true`
and the log will contain a full member dump to update the candidate lists from.

## Building

1. Edit `<GamePath>` in `SynthDiscordRPC.csproj` to your Synth Riders install.
2. `dotnet build -c Release`
3. Copy `SynthDiscordRPC.dll` to `<game>/Mods/`.

## Troubleshooting

- **"No Discord Application ID available" warning** — the release was built without
  `DefaultClientId` set; do the maintainer setup above or set `ClientId` in the config.
- **Presence never appears** — check Discord's *Settings → Activity Privacy →
  Share your detected activities with others* is on, and that Discord (not just the
  browser version) is running.
- **Song shows as "Unknown Song"** — enable `DebugLogging`, play a song, and check
  the member dump in the MelonLoader console; the metadata field names may have
  changed on that branch.
