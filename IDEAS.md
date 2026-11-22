# Feature ideas

Ideas for later, roughly in order of usefulness. Done so far: Fix Seeking for fragmented MP4 / WebM videos (#6); versioned script links
(`Url.Versioned`), so browsers pick up new scripts after a deploy; busy buttons reset themselves if the
page's script crashes (`MediaTools.busy` / `idle`); Fix Seeking checks its copy with ffprobe and the copy's
own headers before it replaces anything.

## Follow-ups from Fix Seeking (#6)

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
