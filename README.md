# MeTube for Jellyfin

Search YouTube inside Jellyfin, press **Add**, and watch the video from your own server, including when the internet is down.

The plugin adds a **YouTube** entry to Jellyfin's menu (web client and the Jellyfin Android app) for the users you choose. They search
YouTube or paste a link, and your [MeTube](https://github.com/alexta69/metube) server downloads the video straight into a Jellyfin library.
It shows up there with its title, thumbnail, description and date.

```
Jellyfin (web / Android app) ── YouTube menu ── search · Add
        │  the user's own Jellyfin login
        ▼
Jellyfin server + this plugin ──LAN──► MeTube ──► <library>/youtube/<Channel>/<Title> [<id>].mkv + .jpg
        ▲                                                   │
        └──────────── library scan, item ready ◄────────────┘
```

## Features

- **Search in Jellyfin.** Result cards show thumbnail, title, channel, length, views and age, with **Load more**. You can also paste any YouTube link.
- **One-click Add.** Each card tracks the download live: Add → Queued → Downloading 42% → Processing → **Play**.
- **Instant library update.** When MeTube finishes, the plugin scans just the YouTube library, so the video appears within seconds.
- **Who may add.** Administrators always can. You pick other users in the settings, and everyone else sees nothing.
- **Limits** for non-admins: the longest allowed video and videos per 24 hours. Live streams and premieres are always refused.
- **Downloads tab.** Users see their recent adds; admins see everyone's.
- **No YouTube API key.** Search uses YouTube's own web API. If YouTube changes its page layout, MeTube's yt-dlp search takes over.
- **MeTube stays private.** Browsers only talk to Jellyfin, and Jellyfin's server talks to MeTube over your LAN.

## Requirements

- Jellyfin **12.2** or newer.
- MeTube 2026.10 or newer, reachable from the Jellyfin server.
- MeTube's download folder (or a subfolder of it) mounted as a Jellyfin library. See [SETUP.md](SETUP.md).

## Install

1. Dashboard → Plugins → Repositories → **+**, URL:
   `https://raw.githubusercontent.com/SurfHost/jellyfin-plugin-metube/main/manifest.json`
2. Catalog → **MeTube** → Install, then restart Jellyfin.
3. Follow [SETUP.md](SETUP.md) for the MeTube settings (output folder, presets) and the Jellyfin library. Then open the plugin settings:
   enter the MeTube URL, pick the library, tick the users, and press **Test connection**.

## Security

- **Plugin endpoints.** Every endpoint requires a signed-in Jellyfin user, plus admin rights or a place on the allowed list. Only the
  client script (no secrets) is public.
- **Download URLs.** These are built server-side from a validated 11-character video ID, so nothing a user types reaches MeTube or yt-dlp.
- **Abuse limits.** Search is rate-limited per user and pauses automatically if YouTube starts throttling the server, so the NAS's
  address stays usable for downloads.
- **MeTube itself** has no login. Keep it off the internet; this plugin is what makes that possible.

## How the menu entry works

Jellyfin only shows plugin pages to administrators, inside the dashboard. To reach normal users, the plugin adds one `<script>` tag to
Jellyfin Web's `index.html` while it is served:

- The tag is added in memory only, never on disk.
- It works with read-only containers such as TrueNAS apps.
- It coexists with other plugins that inject scripts.

If a future Jellyfin release breaks the entry, untick **Inject the YouTube menu entry** in the settings. Admins can still use
**Open YouTube search** there.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build Jellyfin.Plugin.MeTube/Jellyfin.Plugin.MeTube.csproj -c Release
dotnet test Jellyfin.Plugin.MeTube.Tests/Jellyfin.Plugin.MeTube.Tests.csproj -c Release
```

Releases are built by GitHub Actions from a `vX.Y.Z` tag (see `.github/workflows/release.yml`).

## License

GPL-3.0. Not affiliated with YouTube, Google, Jellyfin or MeTube.
