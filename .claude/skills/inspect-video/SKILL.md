---
name: inspect-video
description: Inspect an MP4 / MOV video's structure: whether it's fragmented, and the lengths stored in its headers versus what ffprobe reads. Use when a video won't seek, shows a wrong length in a player, or Fix Seeking (Models/VideoIndex.cs) misbehaves.
---

# Inspect a video

Two readings of the same file, compared:

- `boxes.ps1` (next to this file) walks the MP4 boxes: top-level layout (`moof` boxes mean fragmented, which `VideoIndex.NeedsFix` rewrites), and the stored lengths in `mvhd`, `tkhd`, `mdhd`, `mehd` and `elst`.
- ffprobe reads the length the way most players do: `E:\ffprobe.exe -v error -show_entries format=duration,start_time:stream=index,codec_type,codec_name,start_time,duration,duration_ts,time_base -of compact "<file>"`

## Steps

1. **Locate the file.** Most media lives in folders that are off limits to the agent: anything under `E:\Images`, `E:\Videos`, `E:\Documents` or a `Program Files` folder. For a file there, hand the user both commands below with the path filled in, and work from the output they paste back. Done when you have both outputs for the file.

   ```powershell
   & "<repo>\.claude\skills\inspect-video\boxes.ps1" "<file>"
   E:\ffprobe.exe -v error -show_entries format=duration,start_time:stream=index,codec_type,codec_name,start_time,duration,duration_ts,time_base -of compact "<file>"
   ```

2. **Compare.** Each stored length divided by its timescale should roughly match ffprobe's duration: `mvhd` and `tkhd` use the movie timescale, `mdhd` the track's own. A rewritten file (no `moof`, `moov` before `mdat`) should have every one filled in. A length of 0 is normal only in a fragmented file. Done when every header length has been checked against ffprobe's duration.
3. **Report** whether the file is fragmented, every header length that is 0 or doesn't match ffprobe, and what that means for `VideoIndex` (needs a fix, rewrite went wrong, or fine).

## Reference

- Unfixed FadCam recordings: a fragmented MP4 (`moov` with `mvex`, then about one `moof` + `mdat` pair per second), and every stored length is 0.
- Windows Photos takes its length from the headers rather than counting frames, so a wrong header shows as a wrong length there even when ffprobe and browsers are fine.
