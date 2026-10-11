# Fixtures

Real responses captured on 2026-10-11 from a residential connection in the Netherlands, so the
parsers are tested against what YouTube and MeTube actually send.

| File | Source |
|---|---|
| `search_me_at_the_zoo.json` | `POST youtube.com/youtubei/v1/search`, query "me at the zoo", videos-only filter, `hl=en`, `gl=NL` |
| `search_continuation.json` | The next page of that search, via its continuation token |
| `search_live.json` | Search for "lofi hip hop radio beats to relax study to"; the first result is a live stream |
| `player_jNQXAC9IVRw.json` | `POST youtube.com/youtubei/v1/player` for "Me at the zoo" (only `playabilityStatus` and `videoDetails` kept) |
| `player_live.json` | The same for the live stream in `search_live.json` |
| `player_unavailable.json` | The same for a non-existent id |
| `metube_browse.json` | MeTube 2026.10.09 `POST /browse` with `{"url":"ytsearch5:me at the zoo"}` |

When YouTube changes its response shape, capture new files the same way. Keep the old ones if the parser
still has to handle them.
