using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            // Video and audio only: phones add data tracks (motion / camera info) that MP4 files can't always hold.
            // -c copy: no re-encoding. Rotation and the recording date come along.
            string faststart = format == "webm" ? "" : "-movflags +faststart ";
            string args = $"-hide_banner -loglevel error -nostdin -i \"{sourcePath}\" -map 0:V -map 0:a? -c copy " +
                          $"-map_metadata 0 {faststart}-f {format} -y \"{targetPath}\"";

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string error = await RunFfmpegAsync(args).ConfigureAwait(false);
                if (error == null && !LooksComplete(sourcePath, targetPath))
                    error = "ffmpeg's copy came out much smaller than the original, so it wasn't used.";
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

        private static async Task<string> RunFfmpegAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = SiteSettings.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    var stderr = process.StandardError.ReadToEndAsync();
                    var exited = Task.Run(() => process.WaitForExit((int)Timeout.TotalMilliseconds));
                    if (!await exited.ConfigureAwait(false))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { } // already gone
                        return "ffmpeg took too long.";
                    }

                    string errors = (await stderr.ConfigureAwait(false)).Trim();
                    return process.ExitCode == 0 ? null : "ffmpeg failed: " + (errors.Length > 0 ? errors : "exit code " + process.ExitCode);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return "Couldn't start ffmpeg: " + ex.Message;
            }
        }

        // Dropping only the index and data tracks barely changes the size; a much smaller file means video was lost.
        private static bool LooksComplete(string sourcePath, string targetPath)
        {
            var target = new FileInfo(targetPath);
            return target.Exists && target.Length >= new FileInfo(sourcePath).Length * 0.8;
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
