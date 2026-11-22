# Feature ideas

Ideas for later, roughly in order of usefulness. Done so far: Fix Seeking for fragmented MP4 / WebM videos (#6).

## Follow-ups from Fix Seeking (#6)

- **Version the script links.** Pages load `~/Scripts/media-common.js` with no version and IIS sends no
  `Cache-Control`, so phones (DDG mobile especially) keep the old script after a deploy. New buttons then
  throw before they reach the server and stick on "Fixing...". Add the file's last-write time to the URL
  (`media-common.js?v=<ticks>`) through a small helper used by all five views that load it, so every
  deploy is picked up straight away.
- **Buttons recover from script errors.** Any button that switches to a busy label ("Fixing...",
  "Deleting...") should go back to normal and show a message if the action throws, not stay busy forever.
- **Check the result after a rewrite.** After ffmpeg rewrites a video, have ffprobe read its length and
  compare it with the original before replacing the file. Windows Photos showed a fixed FadCam video as
  "123299:11" long instead of 6:50. FadCam's own files store a length of 0 in `mvhd` / `tkhd` / `mdhd`
  (it's in the fragments instead), so the rewritten header is the first thing to look at.
- **Fix FadCam videos in bulk.** Either set FadCam to save ordinary MP4s, if it has that option, or add a
  "fix every video in this folder that needs it" action (Gallery's Select mode does this one page at a time).

## Seeing what the server did

- **Activity log page.** Recent uploads, fixes, moves, deletes and errors, owner only. Today warnings go
  to `Trace` and the IIS logs need admin rights, so "is it stuck or still working?" can't be answered from
  the site itself.

## Library tidying

- **Find duplicates.** Group files with the same size and content hash, e.g. `Name (2).mp4` next to
  `Name.mp4`, and let the owner keep one and send the rest to the Recycle Bin.

## Deploying

- **One command for both sites.** MediaManager and SecretManager run the same build. Publish to both
  profiles, compare the DLL hash in each site folder with the build, and check each login page answers.
  `deploy/Publish-All.ps1` with no `-Profile` already publishes every folder profile; it's the checks
  that are missing. The `deploy` skill in `.claude/skills/` does the checks by hand for now.
