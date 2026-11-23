# Feature ideas

Ideas for later, roughly in order of usefulness. Done so far: Fix Seeking for fragmented MP4 / WebM videos (#6); versioned script links
(`Url.Versioned`), so browsers pick up new scripts after a deploy; busy buttons reset themselves if the
page's script crashes (`MediaTools.busy` / `idle`); Fix Seeking checks its copy with ffprobe and the copy's
own headers before it replaces anything; AVI videos play in the browser through an MP4 copy
(`Models/PlayableCopy.cs`); the Duplicates page (`Models/DuplicateFinder.cs`); the Activity page
(`Models/ActivityLog.cs`); the Empty Folders page (`Models/EmptyFolders.cs`); Copy (next to Move); cancelling one
upload in the Add Media queue; tags (`Models/Tags.cs`, Tags page, Gallery tag filter, Select > Tag); the Storage
and Recently Added pages (`Models/LibraryIndex.cs`); for guests, shared folders (`Models/SharedFolders.cs`,
"Shared with me") and the Guestbook (`Models/Guestbook.cs`) (#26).

## Finding things

- **Search the whole library by name.** A search box in the menu that finds files in any folder. The Gallery's
  search only covers the folder it's showing (and its subfolders with "Include subfolders").

## Watching

- **Remember where you stopped.** For long videos, pick up where you left off, on any device (save the position
  now and then while it plays, per file).

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

## Follow-ups from Duplicates

- **Similar, not identical.** The Duplicates page only finds byte-for-byte copies. The same photo resized or
  re-saved by a phone app would need a perceptual hash (the thumbnails could be the input).

## Guests

More reasons for a guest to log in. Guests only reach actions marked `[GuestAllowed]` and, under `/media`,
the guest uploads folder and shared folders (`Filters/MediaAuthModule.cs`), so each idea needs those kept tight.

- **Link drop.** Guests paste a link (a video, a song, an article) with a note for the owner. The owner's list
  has *Send to Video / Audio Downloader* (prefill the downloader's `url` box from a `?url=` query string),
  *Done* and *Delete*. Log new links in Activity, like the Guestbook.
- **Reactions and comments on uploads.** Guests (and the owner) heart and leave short comments on images on the
  Guest Uploads wall; new ones show in Activity.
- **Manage your own uploads.** Guests fix the caption of, or delete (to the Recycle Bin), images they uploaded
  themselves, with a "My uploads" view. Needs a way to know whose upload it is (all guests share one password,
  so per browser).
- **Nicer wall.** A full-screen slideshow (keyboard / swipe), "new since your last visit" badges, and a
  Download button on the Guest Uploads wall.
- **Shared lists.** Shopping or to-do lists that guests and the owner both add to and tick off.
- **Events.** Upcoming dates guests can see and say they're coming to.

## Follow-ups from shared folders (#26)

- **Keep shares on rename / move.** Renaming or moving a shared folder through the site doesn't carry the
  share over (`SharedFolders` stores the path); it has to be shared again. Deleting one leaves a stale row,
  which the Shared with me page just skips.

## Deploying

- **One command for both sites.** MediaManager and SecretManager run the same build. Publish to both
  profiles, compare the DLL hash in each site folder with the build, and check each login page answers.
  `deploy/Publish-All.ps1` with no `-Profile` already publishes every folder profile; it's the checks
  that are missing. The `deploy` skill in `.claude/skills/` does the checks by hand for now.
