# Changelog

## [0.3.1] - 2026-10-12

- A logo for the plugin, shown in Jellyfin's plugin catalog and on the installed plugins page.

## [0.3.0] - 2026-10-11

- Merge folders from Jellyfin's own library view: select two or more YouTube channel folders (long-press or right-click a folder), open the "..." menu and choose "Merge folders". The YouTube page's Folders tab opens with those folders selected; pick the folder to keep and confirm. Administrators only.
- "Newest first" is now the default sort for search results. It orders the results loaded so far; choose Relevance for YouTube's own order.
- "Added videos" cleans itself up: videos deleted from the library (with the plugin's Delete, in Jellyfin's own menu, or on disk) disappear from the list. Failed or lost downloads get a remove button, and "Clean up list" removes them all at once. The entries are only hidden: failed adds still count toward the daily limit.
- Fixed: on a phone, the rows in "Added videos" were squeezed so narrow that the title disappeared; the buttons now move to a line of their own.

## [0.2.0] - 2026-10-11

- YouTube is now a page inside Jellyfin instead of a separate full-screen window: the header and navigation stay, there is no close button, and Back, the Android back button and page reloads all work. The address (`#/youtube?q=…`) keeps the search, sort and filters.
- Sorting and filters on the search page: sort by relevance, popularity, newest, shortest or longest. Filter by length (under 3 minutes, 3 to 20 minutes, over 20 minutes) and by upload date (today, this week, this month, this year). YouTube itself can only filter by length, so shortest, longest and newest reorder the results loaded so far.
- Delete: whoever added a video, and administrators, can delete it from the library and from disk (after a confirmation), from the search results or the "Added videos" tab. Jellyfin needs write access to the YouTube folder for this; Test connection checks it.
- Merge channel folders (administrators): a new Folders tab lists the channel folders. Select two or more, choose Merge, and pick the folder to keep. All videos move into it, the emptied folders are removed, and later downloads from the merged channels go into the kept folder too. A file whose name already exists in the kept folder is left where it was. Moved videos lose their watched status in Jellyfin.
- Black bars on the YouTube library tile: when a video has no HD thumbnail, YouTube's 4:3 thumbnail with black bars was used. Such thumbnails are now cropped to 16:9, for new downloads and for videos already in the library, and the library tile is rebuilt. Thumbnails of real 4:3 videos are left alone.
- MeTube's temp folder (`.metube-tmp`) is cleaned up: the empty channel folders yt-dlp leaves behind are removed while MeTube is idle. Files are never removed. The folder name is a new setting.
- "Newest first" also works when YouTube answers in Dutch.
- The page follows the selected Jellyfin theme.
- Fixed: opening a Jellyfin dialog on the page (such as the Delete confirmation) re-ran the search and lost loaded results; the browser title could read "Page not found"; Add and Back could scroll the page to the top; a user who signs in on the same tab could briefly see the previous user's results.
- Fixed: Delete buttons now follow exactly the rule the server enforces; a Delete while the library folder is unreachable no longer forgets who added the video; when a video is in the library twice, Delete removes the adder's own copy.
- Fixed: a large library no longer pushes recent failed downloads out of the job list, which kept them from counting toward the daily limit.

## [0.1.2] - 2026-10-11

- Fixed: the YouTube menu entry did not appear when another plugin also adds its script to Jellyfin's start page by answering that page itself (GuestPass 0.2.x does this). MeTube now wraps such plugins instead of being skipped by them, and both scripts load.
- Test connection now reports when no page with the menu entry has been served since startup.

## [0.1.1] - 2026-10-11

- Setup guide: the library folder and MeTube address are described generically instead of naming one particular drive letter and port. No code changes.

## [0.1.0] - 2026-10-11

- First release, for Jellyfin 12.2.
- Selected users get a YouTube entry in the Jellyfin menu (web and the Android app). It lets them search YouTube or paste a link, then press Add, and your MeTube server downloads the video straight into a Jellyfin library.
- Each card shows live progress (Queued, Downloading, Processing) and ends with a Play button once Jellyfin has the video. The library is scanned right after the download finishes.
- Admins choose who may add videos, the longest allowed video and a daily limit per user. Live streams and premieres are refused.
- Search needs no YouTube API key. If YouTube changes its search page, MeTube's own yt-dlp search takes over.
- MeTube stays on your LAN: Jellyfin's login protects every request.
