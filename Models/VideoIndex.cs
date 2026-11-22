using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaManager.Models
{
    /// <summary>
    /// Makes videos seekable in desktop players (Windows Photos / Media Player hide the seek bar otherwise).
    ///
    /// Some recorders save MP4s in fragments so a crash doesn't lose the recording (FadCam, OBS, many screen
    /// recorders and downloaders); browser / screen recordings saved as WebM often have no length or index.
    /// ffmpeg rewrites the file with a proper index and the whole-video length at the front, copying the video
    /// and audio as they are: no re-encoding, no quality loss, and about as fast as copying the file.
    ///
    /// New uploads are checked (NeedsFix) and only rewritten when they need it, so ordinary camera videos are
    /// kept byte for byte. The Fix Seeking button rewrites the file it's asked to.
    /// </summary>
    public static class VideoIndex
    {
        /// <summary>Formats ffmpeg can rewrite without re-encoding, and its name for each.</summary>
        private static readonly Dictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".mp4", "mp4" },
            { ".mov", "mov" },
            { ".webm", "webm" },
        };

        /// <summary>Longest a rewrite may take (it's mostly disk speed; a few GB takes well under a minute).</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

        /// <summary>Rewrites at once (each reads and writes a whole video).</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(2);

        public static bool CanFix(string ext) => ext != null && Formats.ContainsKey(ext);

        /// <summary>
        /// True if a video of this type should be rewritten: an MP4 / MOV saved in fragments or without its index,
        /// or any WebM (their length and index are often missing, and the rewrite is cheap). False for normal
        /// MP4s, including camera videos with the index at the end (desktop players seek those fine).
        /// </summary>
        public static bool NeedsFix(string path, string ext)
        {
            if (!CanFix(ext))
                return false;
            if (string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return !HasPlainIndex(fs);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Walks the top-level MP4 boxes: true if there's a "moov" (the index), it isn't fragmented ("moov" has no
        /// "mvex" and there are no "moof" boxes), and nothing in the box sizes is broken.
        /// </summary>
        private static bool HasPlainIndex(Stream s)
        {
            long length = s.Length;
            long pos = 0;
            bool hasMoov = false;

            while (pos + 8 <= length)
            {
                if (!ReadBoxHeader(s, pos, length, out long size, out string type, out int header))
                    return false;

                if (type == "moof")
                    return false;
                if (type == "moov")
                {
                    if (HasChild(s, pos + header, pos + size, "mvex"))
                        return false;
                    hasMoov = true;
                }

                pos += size;
            }

            return hasMoov;
        }

        private static bool HasChild(Stream s, long start, long end, string wanted)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                if (!ReadBoxHeader(s, pos, end, out long size, out string type, out _))
                    return false;
                if (type == wanted)
                    return true;
                pos += size;
            }
            return false;
        }

        /// <summary>Reads the box at pos. False if it runs past end or its size makes no sense (cut off / not an MP4).</summary>
        private static bool ReadBoxHeader(Stream s, long pos, long end, out long size, out string type, out int header)
        {
            size = 0;
            type = null;
            header = 8;

            var buf = new byte[16];
            s.Position = pos;
            if (ReadFully(s, buf, 8) < 8)
                return false;

            size = ((long)buf[0] << 24) | ((long)buf[1] << 16) | ((long)buf[2] << 8) | buf[3];
            type = Encoding.ASCII.GetString(buf, 4, 4);

            if (size == 1) // 64-bit size follows
            {
                if (ReadFully(s, buf, 8) < 8)
                    return false;
                size = 0;
                for (int i = 0; i < 8; i++)
                    size = (size << 8) | buf[i];
                header = 16;
            }
            else if (size == 0) // runs to the end
            {
                size = end - pos;
            }

            return size >= header && pos + size <= end;
        }

        private static int ReadFully(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n == 0)
                    break;
                total += n;
            }
            return total;
        }

        /// <summary>
        /// Writes a seekable copy of <paramref name="sourcePath"/> to <paramref name="targetPath"/>, in the format
        /// <paramref name="ext"/> names (the source's own name doesn't matter, so an upload's ".part" file works).
        /// Returns null when it worked, or why it didn't (the target is removed then).
        /// </summary>
        public static async Task<string> RewriteAsync(string sourcePath, string targetPath, string ext)
        {
            if (!Formats.TryGetValue(ext ?? "", out string format))
                return "Only MP4, MOV and WebM videos can be fixed this way.";
            if (!File.Exists(SiteSettings.FfmpegPath))
                return "ffmpeg wasn't found at " + SiteSettings.FfmpegPath + ".";
            if (!File.Exists(SiteSettings.FfprobePath))
                return "ffprobe wasn't found at " + SiteSettings.FfprobePath + " (it checks the fixed copy before anything is replaced).";

            // Video and audio only: phones add data tracks (motion / camera info) that MP4 files can't always hold.
            // -c copy: no re-encoding. Rotation and the recording date come along.
            string faststart = format == "webm" ? "" : "-movflags +faststart ";
            string args = $"-hide_banner -loglevel error -nostdin -i \"{sourcePath}\" -map 0:V -map 0:a? -c copy " +
                          $"-map_metadata 0 {faststart}-f {format} -y \"{targetPath}\"";

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string error = (await RunToolAsync(SiteSettings.FfmpegPath, args).ConfigureAwait(false)).Error;
                if (error == null)
                    error = await CheckCopyAsync(sourcePath, targetPath, format).ConfigureAwait(false);
                if (error != null)
                    TryDelete(targetPath);
                return error;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Rewrites a video in its folder. The new file replaces the old one only once it's complete, and keeps
        /// its name, created and modified dates. Returns null when it worked, or why it didn't.
        /// </summary>
        public static async Task<string> FixInPlaceAsync(string path)
        {
            string ext = Path.GetExtension(path);
            string temp = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + ".fixing.tmp");

            string error = await RewriteAsync(path, temp, ext).ConfigureAwait(false);
            if (error != null)
                return error;

            try
            {
                var original = new FileInfo(path);
                DateTime modified = original.LastWriteTimeUtc;

                // Replace keeps the original's created date, attributes and permissions
                File.Replace(temp, path, null);
                File.SetLastWriteTimeUtc(path, modified);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(temp);
                return "Couldn't replace the file (is it open somewhere?): " + ex.Message;
            }
        }

        // ---------- checking the copy before it's used ----------

        /// <summary>
        /// Checks ffmpeg's copy before it replaces anything: it's about the original's size (dropping only the index
        /// and data tracks barely changes it; much smaller means video was lost), ffprobe reads the same length for
        /// both, and for MP4 / MOV the lengths in the copy's headers match too. Windows Photos and Media Player show
        /// the header's length, which ffprobe mostly works out from the video itself, so a copy can play fine
        /// everywhere else and still show a wrong length there. Returns null if the copy is good, or why not.
        /// </summary>
        private static async Task<string> CheckCopyAsync(string sourcePath, string targetPath, string format)
        {
            var target = new FileInfo(targetPath);
            if (!target.Exists || target.Length < new FileInfo(sourcePath).Length * 0.8)
                return "ffmpeg's copy came out much smaller than the original, so it wasn't used.";

            double? copyLength = await ProbeLengthAsync(targetPath).ConfigureAwait(false);
            if (copyLength == null || copyLength <= 0)
                return "ffprobe couldn't read a length from the fixed copy, so it wasn't used.";

            // A WebM being fixed often has no length of its own (that's the problem); its copy's is checked against itself below
            double? originalLength = await ProbeLengthAsync(sourcePath).ConfigureAwait(false);
            double expected = originalLength ?? copyLength.Value;
            if (originalLength != null && !Close(copyLength.Value, expected))
                return $"The fixed copy is {Describe(copyLength.Value)} long but the original is {Describe(expected)}, so it wasn't used.";

            if (format == "webm")
                return null;

            string headerError = CheckHeaderLengths(targetPath, expected);
            return headerError == null ? null : headerError + ", so it wasn't used.";
        }

        /// <summary>Lengths within 1 second or 2% of each other, whichever is more.</summary>
        internal static bool Close(double seconds, double expected) => Math.Abs(seconds - expected) <= Math.Max(1.0, expected * 0.02);

        private static string Describe(double seconds)
        {
            if (seconds >= 100 * 3600)
                return Math.Round(seconds / 3600) + " hours";
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t.Minutes}:{t:ss}";
        }

        /// <summary>The length ffprobe reads, in seconds, or null if it reads none (e.g. a WebM without one).</summary>
        internal static async Task<double?> ProbeLengthAsync(string path)
        {
            var result = await RunToolAsync(SiteSettings.FfprobePath,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"").ConfigureAwait(false);
            return result.Error == null && double.TryParse(result.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : (double?)null;
        }

        /// <summary>
        /// Checks the lengths written in an MP4 / MOV's headers: the movie header (mvhd, the length desktop players
        /// show) must match <paramref name="expected"/>, and each track's media header (mdhd) must be filled in and
        /// no longer than that. Returns null if they're right, or what's wrong.
        /// </summary>
        private static string CheckHeaderLengths(string path, double expected)
        {
            double? movie = null;
            var tracks = new List<double>();

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    foreach (var moov in Children(fs, 0, fs.Length, "moov"))
                    {
                        foreach (var mvhd in Children(fs, moov.Start, moov.End, "mvhd"))
                            movie = ReadHeaderLength(fs, mvhd.Start);
                        foreach (var trak in Children(fs, moov.Start, moov.End, "trak"))
                            foreach (var mdia in Children(fs, trak.Start, trak.End, "mdia"))
                                foreach (var mdhd in Children(fs, mdia.Start, mdia.End, "mdhd"))
                                    tracks.Add(ReadHeaderLength(fs, mdhd.Start) ?? -1);
                    }
                }
            }
            catch (IOException ex)
            {
                return "Couldn't read the fixed copy's headers (" + ex.Message + ")";
            }

            if (movie == null || tracks.Count == 0)
                return "The fixed copy has no length in its headers";
            if (!Close(movie.Value, expected))
                return $"The fixed copy's header says it's {Describe(movie.Value)} long (what Windows Photos would show) but it's really {Describe(expected)}";
            foreach (double track in tracks)
                if (track <= 0 || track > expected + Math.Max(1.0, expected * 0.02))
                    return $"A track in the fixed copy says it's {(track <= 0 ? "empty" : Describe(track) + " long")} but the video is {Describe(expected)}";
            return null;
        }

        /// <summary>
        /// Seconds stored in an mvhd / mdhd box (same layout: version, flags, two dates, timescale, duration), or null
        /// if it's unset or marked unknown (all ones).
        /// </summary>
        private static double? ReadHeaderLength(Stream s, long contentStart)
        {
            var buf = new byte[32];
            s.Position = contentStart;
            if (ReadFully(s, buf, 32) < 32)
                return null;

            bool v1 = buf[0] == 1;
            int at = v1 ? 20 : 12;                          // 1 version + 3 flags + two 8-byte (v1) or 4-byte (v0) dates
            ulong timescale = ReadUInt(buf, at, 4);
            ulong duration = ReadUInt(buf, at + 4, v1 ? 8 : 4);
            bool unknown = v1 ? duration == ulong.MaxValue : duration == uint.MaxValue;
            return timescale == 0 || unknown ? (double?)null : (double)duration / timescale;
        }

        private static ulong ReadUInt(byte[] buf, int at, int bytes)
        {
            ulong value = 0;
            for (int i = 0; i < bytes; i++)
                value = (value << 8) | buf[at + i];
            return value;
        }

        private struct Box
        {
            public long Start; // first byte after the box's header
            public long End;
        }

        /// <summary>The boxes of type <paramref name="type"/> directly inside start..end (stops at a broken box).</summary>
        private static IEnumerable<Box> Children(Stream s, long start, long end, string type)
        {
            long pos = start;
            while (pos + 8 <= end && ReadBoxHeader(s, pos, end, out long size, out string found, out int header))
            {
                if (found == type)
                    yield return new Box { Start = pos + header, End = pos + size };
                pos += size;
            }
        }

        // ---------- running ffmpeg / ffprobe ----------

        internal class ToolResult
        {
            public string Error;  // null if it worked
            public string Output; // what it printed (ffprobe's answer)
        }

        internal static async Task<ToolResult> RunToolAsync(string exe, string args)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var exited = Task.Run(() => process.WaitForExit((int)Timeout.TotalMilliseconds));
                    if (!await exited.ConfigureAwait(false))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { } // already gone
                        return new ToolResult { Error = name + " took too long." };
                    }

                    string output = await stdout.ConfigureAwait(false);
                    string errors = (await stderr.ConfigureAwait(false)).Trim();
                    return new ToolResult
                    {
                        Output = output,
                        Error = process.ExitCode == 0 ? null : name + " failed: " + (errors.Length > 0 ? errors : "exit code " + process.ExitCode)
                    };
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new ToolResult { Error = "Couldn't start " + name + ": " + ex.Message };
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
