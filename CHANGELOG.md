# Changelog

## [0.1.0] - 2026-10-11

- First release, for Jellyfin 12.2.
- Selected users get a YouTube entry in the Jellyfin menu (web and the Android app). It lets them search YouTube or paste a link, then press Add, and your MeTube server downloads the video straight into a Jellyfin library.
- Each card shows live progress (Queued, Downloading, Processing) and ends with a Play button once Jellyfin has the video. The library is scanned right after the download finishes.
- Admins choose who may add videos, the longest allowed video and a daily limit per user. Live streams and premieres are refused.
- Search needs no YouTube API key. If YouTube changes its search page, MeTube's own yt-dlp search takes over.
- MeTube stays on your LAN: Jellyfin's login protects every request.
