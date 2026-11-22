# Feature ideas

Ideas for later, roughly in order of usefulness. Done so far: Fix Seeking for fragmented MP4 / WebM videos (#6); versioned script links
(`Url.Versioned`), so browsers pick up new scripts after a deploy; busy buttons reset themselves if the
page's script crashes (`MediaTools.busy` / `idle`); Fix Seeking checks its copy with ffprobe and the copy's
own headers before it replaces anything; AVI videos play in the browser through an MP4 copy
(`Models/PlayableCopy.cs`); the Duplicates page (`Models/DuplicateFinder.cs`).

## Follow-ups from Fix Seeking (#6)

- **Fix FadCam videos in bulk.** Either set FadCam to save ordinary MP4s, if it has that option, or add a
  "fix every video in this folder that needs it" action (Gallery's Select mode does this one page at a time).

## Follow-ups from AVI playback

- **Clean up old playable copies.** Copies in `<media root>\.playable\` are removed when their AVI is
  deleted, renamed, moved or fixed through the site, but not when its whole folder is deleted or the AVI is
  deleted or moved outside the site. A cleanup that drops copies whose AVI is gone (and shows the folder's size)
  would keep it from growing.
- **More formats.** `PlayableCopy.Extensions` only lists `.avi`. MKV, WMV and FLV could use the same path once
  they're added to `Constants.VideoExtensions` (and given a `<mimeMap>` in Web.config).

## Seeing what the server did

- **Activity log page.** Recent uploads, fixes, moves, deletes and errors, owner only. Today warnings go
  to `Trace` and the IIS logs need admin rights, so "is it stuck or still working?" can't be answered from
  the site itself.

## Follow-ups from Duplicates

- **Similar, not identical.** The Duplicates page only finds byte-for-byte copies. The same photo resized or
  re-saved by a phone app would need a perceptual hash (the thumbnails could be the input).

## Deploying

- **One command for both sites.** MediaManager and SecretManager run the same build. Publish to both
  profiles, compare the DLL hash in each site folder with the build, and check each login page answers.
  `deploy/Publish-All.ps1` with no `-Profile` already publishes every folder profile; it's the checks
  that are missing. The `deploy` skill in `.claude/skills/` does the checks by hand for now.
