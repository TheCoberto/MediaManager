using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace MediaManager.Models
{
    /// <summary>
    /// Browsers can't play AVI, so the first time one is opened, ffmpeg makes an MP4 copy they can. Copies are kept
    /// in &lt;media root&gt;\.playable\ (one per video, named by a hash of its path, like Models/Thumbnails.cs) and served
    /// from /media like any other file, so they stream and seek and stay behind the login. A copy is stamped with the
    /// original's modified time and remade if the original changes. The original is never touched.
    ///
    /// H.264 video inside the AVI is copied as it is (a few seconds); anything else (DivX, Xvid, MJPEG...) is converted
    /// to H.264 / AAC. That can take a while for long videos, so it runs in the background, one video at a time, and
    /// the page polls for progress (Controllers/PlayableController.cs).
    /// </summary>
    public static class PlayableCopy
    {
        public const string FolderName = ".playable";

        /// <summary>Video types browsers can't play, shown through a copy (the browser gets this list too).</summary>
        public static readonly string[] Extensions = { ".avi" };

        private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(30);

        /// <summary>Conversions at once: each one keeps several CPU cores busy.</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);

        /// <summary>Copies being made or waiting their turn, by the copy's path.</summary>
        private static readonly ConcurrentDictionary<string, Job> Jobs = new ConcurrentDictionary<string, Job>(StringComparer.OrdinalIgnoreCase);

        private class Job
        {
            public volatile int Percent;
            public volatile bool Started;
        }

        public class Status
        {
            /// <summary>"ready", "working", "waiting" (another video is being converted), "failed" or "none" (not started).</summary>
            public string State;
            public string Url;      // ready: the copy's URL
            public int Percent;     // working
            public string Message;  // failed: why
        }

        public static bool IsNeeded(string fileName) =>
            Extensions.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());

        public static Status GetStatus(string sourcePath, string folder, string fileName)
        {
            var source = new FileInfo(sourcePath);
            string copy = PathFor(folder, fileName);

            if (IsCurrent(copy, source.LastWriteTimeUtc))
                return new Status { State = "ready", Url = MediaPaths.UrlFor(MediaPaths.ToRelative(copy)) + "?v=" + source.LastWriteTimeUtc.Ticks.ToString("x") };
            if (Jobs.TryGetValue(copy, out var job))
                return job.Started ? new Status { State = "working", Percent = job.Percent } : new Status { State = "waiting" };
            if (MemoryCache.Default.Get(FailKey(copy, source)) is string error)
                return new Status { State = "failed", Message = error };
            return new Status { State = "none" };
        }

        /// <summary>Starts making the copy in the background unless it's ready, under way or recently failed (retry: try again anyway).</summary>
        public static Status Start(string sourcePath, string folder, string fileName, bool retry)
        {
            var source = new FileInfo(sourcePath);
            string copy = PathFor(folder, fileName);
            if (retry)
                MemoryCache.Default.Remove(FailKey(copy, source));

            var status = GetStatus(sourcePath, folder, fileName);
            if (status.State != "none")
                return status;

            var job = new Job();
            if (Jobs.TryAdd(copy, job))
                HostingEnvironment.QueueBackgroundWorkItem(ct => RunAsync(sourcePath, copy, source, job, ct));
            return GetStatus(sourcePath, folder, fileName);
        }

        /// <summary>The file was renamed, moved, deleted or rewritten through the site: drop its copy.</summary>
        public static void Forget(string folder, string fileName)
        {
            if (IsNeeded(fileName) && MediaPaths.IsPlainFileName(fileName))
                TryDelete(PathFor(folder, fileName));
        }

        private static async Task RunAsync(string sourcePath, string copy, FileInfo source, Job job, CancellationToken ct)
        {
            string error;
            bool entered = false;
            var timer = new Stopwatch();
            try
            {
                await Gate.WaitAsync(ct).ConfigureAwait(false);
                entered = true;
                job.Started = true;
                timer.Start();
                error = await MakeAsync(sourcePath, copy, source.LastWriteTimeUtc, p => job.Percent = p, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                error = "The site restarted while this video was being converted. Try again.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                error = ex.Message;
            }
            finally
            {
                if (entered)
                    Gate.Release();
            }

            // Note the failure before the job goes, so the page never sees "none" in between and starts it again
            string relative = MediaPaths.ToRelative(sourcePath);
            int slash = relative.LastIndexOf('/');
            string folder = slash < 0 ? "" : relative.Substring(0, slash);
            if (error != null)
            {
                MemoryCache.Default.Set(FailKey(copy, source), error, DateTimeOffset.Now.Add(RetryFailedAfter));
                ActivityLog.Error(ActivityLog.Playable, "Couldn't make a copy browsers can play: " + error, folder, source.Name);
            }
            else
            {
                ActivityLog.Write(ActivityLog.Playable, $"Made a copy browsers can play ({ActivityLog.FormatBytes(new FileInfo(copy).Length)}, "
                    + $"took {Math.Max(1, (int)timer.Elapsed.TotalSeconds)}s).", folder, source.Name);
            }
            Jobs.TryRemove(copy, out _);
        }

        /// <summary>
        /// Makes the copy at <paramref name="copyPath"/> (replacing any older one). Tries copying H.264 as it is first
        /// when the AVI has it, and converts if that doesn't work. Returns null when it worked, or why it didn't.
        /// </summary>
        internal static async Task<string> MakeAsync(string sourcePath, string copyPath, DateTime sourceTimeUtc, Action<int> progress, CancellationToken ct)
        {
            if (!File.Exists(SiteSettings.FfmpegPath))
                return "ffmpeg wasn't found at " + SiteSettings.FfmpegPath + ".";
            if (!File.Exists(SiteSettings.FfprobePath))
                return "ffprobe wasn't found at " + SiteSettings.FfprobePath + ".";

            double? length = await VideoIndex.ProbeLengthAsync(sourcePath).ConfigureAwait(false);
            var streams = await ProbeStreamsAsync(sourcePath).ConfigureAwait(false);
            var video = streams.FirstOrDefault(s => s.Type == "video");
            if (video == null)
                return "ffprobe found no video in this file (it may be damaged).";
            var audio = streams.FirstOrDefault(s => s.Type == "audio");

            // Browsers play H.264 in 8-bit 4:2:0 only; other H.264 (and every other codec) gets converted
            bool copyVideo = video.Codec == "h264" && video.PixelFormat == "yuv420p";
            string audioArgs = audio == null ? "" : audio.Codec == "aac" ? "-c:a copy" : "-c:a aac -b:a 160k -ac 2";
            const string convertVideo = "-c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\"";

            var attempts = new List<string>();
            if (copyVideo)
                attempts.Add("-c:v copy");
            attempts.Add(convertVideo);

            Directory.CreateDirectory(Path.GetDirectoryName(copyPath));
            string temp = copyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string error = null;
            try
            {
                foreach (string videoArgs in attempts)
                {
                    string args = $"-hide_banner -loglevel error -nostdin -nostats -progress pipe:1 -fflags +genpts -i \"{sourcePath}\" " +
                                  $"-map 0:v:0 -map 0:a:0? {videoArgs} {audioArgs} -movflags +faststart -f mp4 -y \"{temp}\"";
                    error = await RunFfmpegAsync(args, length, progress, ct).ConfigureAwait(false);

                    if (error == null)
                    {
                        double? copyLength = await VideoIndex.ProbeLengthAsync(temp).ConfigureAwait(false);
                        if (copyLength == null || copyLength <= 0)
                            error = "The converted copy came out empty.";
                        else if (length != null && !VideoIndex.Close(copyLength.Value, length.Value))
                            error = $"The converted copy is {copyLength:0}s long but the video is {length:0}s.";
                    }
                    if (error == null)
                        break;
                    TryDelete(temp);
                }
                if (error != null)
                    return error;

                // Stamped with the original's time: the copy is current while the two match
                File.SetLastWriteTimeUtc(temp, sourceTimeUtc);
                if (File.Exists(copyPath))
                    File.Delete(copyPath);
                File.Move(temp, copyPath);
                temp = null;
                progress(100);
                return null;
            }
            finally
            {
                if (temp != null)
                    TryDelete(temp);
            }
        }

        private class StreamInfo
        {
            public string Type, Codec, PixelFormat;
        }

        private static async Task<List<StreamInfo>> ProbeStreamsAsync(string path)
        {
            var result = await VideoIndex.RunToolAsync(SiteSettings.FfprobePath,
                $"-v error -show_entries stream=codec_type,codec_name,pix_fmt -of default \"{path}\"").ConfigureAwait(false);

            // [STREAM] / key=value lines / [/STREAM] blocks
            var streams = new List<StreamInfo>();
            StreamInfo current = null;
            foreach (string raw in (result.Output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "[STREAM]")
                    streams.Add(current = new StreamInfo());
                else if (current != null && line.StartsWith("codec_type="))
                    current.Type = line.Substring(11);
                else if (current != null && line.StartsWith("codec_name="))
                    current.Codec = line.Substring(11);
                else if (current != null && line.StartsWith("pix_fmt="))
                    current.PixelFormat = line.Substring(8);
            }
            return streams;
        }

        /// <summary>Runs ffmpeg, reporting progress from its -progress output. Returns null when it worked, or why it didn't.</summary>
        private static async Task<string> RunFfmpegAsync(string args, double? length, Action<int> progress, CancellationToken ct)
        {
            // Converting runs at several times real speed on most PCs; allow plenty for slow ones and long videos
            var timeout = length == null ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(length.Value * 4) + TimeSpan.FromMinutes(10);

            var psi = new ProcessStartInfo
            {
                FileName = SiteSettings.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    // "out_time_us=12345678" (older ffmpeg: out_time_ms, also in microseconds)
                    string line = e.Data;
                    if (line == null || length == null || length <= 0 || !(line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")))
                        return;
                    if (long.TryParse(line.Substring(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us > 0)
                        progress((int)Math.Min(99, us / 1e6 / length.Value * 100));
                };

                process.Start();
                process.BeginOutputReadLine();
                var stderr = process.StandardError.ReadToEndAsync();

                using (ct.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    var exited = Task.Run(() => process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)));
                    if (!await exited.ConfigureAwait(false))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { } // already gone
                        return "ffmpeg took too long.";
                    }
                    process.WaitForExit(); // let the progress output finish
                }

                ct.ThrowIfCancellationRequested();
                string errors = (await stderr.ConfigureAwait(false)).Trim();
                return process.ExitCode == 0 ? null : "ffmpeg failed: " + (errors.Length > 0 ? errors : "exit code " + process.ExitCode);
            }
        }

        private static bool IsCurrent(string copy, DateTime sourceTimeUtc)
        {
            try
            {
                var info = new FileInfo(copy);
                return info.Exists && info.Length > 0 && info.LastWriteTimeUtc == sourceTimeUtc;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string FailKey(string copy, FileInfo source) =>
            "PlayableFailed|" + copy + "|" + source.LastWriteTimeUtc.Ticks + "|" + source.Length;

        /// <summary>.playable\ab\ab12...ef.mp4: a hash of "folder/file" (lower case, as Windows names aren't case-sensitive).</summary>
        private static string PathFor(string folder, string fileName)
        {
            string key = (MediaPaths.NormalizeFolder(folder) + "/" + fileName).ToLowerInvariant();
            string hash;
            using (var sha = SHA1.Create())
                hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2")));
            return Path.Combine(MediaPaths.Root, FolderName, hash.Substring(0, 2), hash + ".mp4");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // in use or no access: it's replaced next time it's needed
            }
        }
    }
}
