# Setup: MeTube + Jellyfin for the MeTube plugin

This sets up the two apps the plugin talks to:

- **MeTube** saves straight into a folder that is a Jellyfin library.
- Every file gets a poster and embedded metadata.
- MeTube stays on your LAN only.

The steps below assume TrueNAS SCALE apps. Adjust the paths for other setups.

## 1. Library folder

Create a plain folder named `youtube` (not a separate dataset) in the dataset Jellyfin already reads, next to `movies` and `shows`.
Over SMB that is `J:\youtube`. It inherits the dataset's permissions. The TrueNAS `apps` user (uid/gid 568) needs write access, which it has
when Radarr/Sonarr already write there.

Check in Jellyfin → Dashboard → Libraries that the folder Jellyfin sees for your other libraries (e.g. `/media/movies`) sits in the same
mount, so `/media/youtube` exists inside Jellyfin too. No existing library should point at the dataset root itself.

## 2. MeTube app (TrueNAS → Apps → metube → Edit)

### Storage → Additional Storage → Add

| Field | Value |
|---|---|
| Type | Host Path |
| Mount Path | `/downloads/youtube` |
| Host Path | `/mnt/<pool>/<Jellyfin dataset>/youtube` (Datasets page shows the exact path) |
| Read Only | off |
| Enable ACL | off |

Keep the existing Downloads storage as it is. MeTube keeps its queue, history and settings there.

### MeTube Configuration → Additional Environment Variables

Add these entries. Copy each value exactly from its code block.

**`TEMP_DIR`** — partial downloads stay in a hidden folder on the same disk. Jellyfin ignores hidden folders, and the finished
file is moved in one atomic rename.
```
/downloads/youtube/.metube-tmp
```

**`OUTPUT_TEMPLATE`** — one folder per channel, file named after the title plus the video id.
```
%(channel,uploader|Unknown channel)s/%(title)s [%(id)s].%(ext)s
```

**`OUTPUT_TEMPLATE_CHANNEL`**
```
%(channel,uploader|Unknown channel)s/%(title)s [%(id)s].%(ext)s
```

**`OUTPUT_TEMPLATE_PLAYLIST`**
```
%(playlist_title)s/%(title)s [%(id)s].%(ext)s
```

**`YTDL_OPTIONS_PRESETS`** — one line, no line breaks. Invalid JSON makes MeTube crash-loop at startup with
"YTDL_OPTIONS_PRESETS is invalid".
```
{"jellyfin":{"writethumbnail":true,"merge_output_format":"mp4/mkv","postprocessors":[{"key":"MetadataFromField","formats":["%(upload_date>%Y-%m-%d)s:%(meta_date)s"],"when":"pre_process"},{"key":"FFmpegThumbnailsConvertor","format":"jpg","when":"before_dl"},{"key":"FFmpegMetadata","add_metadata":true,"add_chapters":true,"add_infojson":false}]},"subtitles":{"writesubtitles":true,"subtitleslangs":["nl.*","en.*"],"subtitlesformat":"vtt/best","ignoreerrors":true}}
```

What the presets do:

- **`jellyfin`**, which the plugin always uses:
  - A `<name>.jpg` poster next to each video.
  - The title, description, chapters and upload date embedded in the file. The date is written as `YYYY-MM-DD`, because Jellyfin misreads yt-dlp's default.
  - A `.mkv` file for VP9/AV1 + Opus, or `.mp4` for H.264 + AAC.
- **`subtitles`**, optional: Dutch and English subtitles as `.vtt` files when the uploader provided them.

Optional, only for downloads made in MeTube's own web page:

**`DEFAULT_FOLDER`**
```
youtube
```

Leave **`ALLOW_YTDL_OPTIONS_OVERRIDES`** unset (false). Save. The app redeploys, and MeTube creates `youtube/.metube-tmp` on start.

> `TEMP_DIR` applies to every MeTube download, including ones you save to other folders. Their temp files are also staged on
> the Jellyfin dataset.

## 3. Make MeTube LAN-only

MeTube has no login. Remove any public hostname that points at it, such as a Cloudflare Tunnel public hostname
(Zero Trust → Networks → Tunnels → your tunnel → Public Hostnames → delete). The plugin reaches MeTube over the LAN
(`http://<nas-ip>:30094`), and Jellyfin's own login protects everything users can do.

## 4. Jellyfin library

Dashboard → Libraries → Add Media Library:

| Setting | Value |
|---|---|
| Content type | **Home Videos and Photos** (no TMDb look-ups, so no wrong matches) |
| Display name | `YouTube` |
| Folder | Jellyfin's path to the folder from step 1, e.g. `/media/youtube` |
| Enable real-time monitoring | on |
| Enable photos | off |
| Prefer embedded titles over filenames | on |

Users who may add videos also need access to this library (Dashboard → Users → user → Access). They need
"Allow remote connections to this server" if they use Jellyfin from outside your network.

## 5. Plugin

Dashboard → Plugins → Repositories → **+**:

- Name: `MeTube`
- URL: `https://raw.githubusercontent.com/SurfHost/jellyfin-plugin-metube/main/manifest.json`

Then Catalog → **MeTube** → Install, and restart Jellyfin. Open the plugin settings:

1. Check the MeTube URL.
2. Pick the YouTube library.
3. Tick the users who may add videos. Admins can always add videos.
4. Press **Test connection**.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| No YouTube entry in the menu | Hard-refresh the page, or clear the app cache in the Android app. Check that you are an allowed user, and that "Inject the menu entry" is on in the plugin settings. |
| Download shows **Failed** | The MeTube error is shown on the card. Open MeTube's web page on the LAN for details. |
| Download shows **Missing** | MeTube's history was cleared before the plugin saw the result, or the file landed somewhere Jellyfin can't see. Check `youtube/<channel>/` on the share. |
| Video downloaded but not in Jellyfin | Channel folders named `metadata`, `subs`, `sample`, or starting with `.`, are ignored by Jellyfin. Rename the folder and scan the library. |
| Leftover files in `youtube/.metube-tmp` | They come from failed or cancelled downloads. Delete them when MeTube's queue is empty. |
| The menu entry breaks after a Jellyfin update | Untick "Inject the menu entry". Admins can still use **Open YouTube search** on the plugin settings page. |
| Can't watch while the internet is down | Your Jellyfin apps connect through the public hostname. Add the LAN address (`http://<nas-ip>:<port>`) as a server in each app. |
