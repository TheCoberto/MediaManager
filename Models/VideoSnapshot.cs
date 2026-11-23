using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaManager.Models
{
    /// <summary>
    /// "Save snapshot" on the View page: saves one frame of a video as a JPEG next to it, at the video's full
    /// resolution. ffmpeg reads the frame from the original file (for an AVI too, not the playable copy), turned
    /// upright by the video's rotation note like the browser shows it, then by any extra turn on screen.
    /// </summary>
    public static class VideoSnapshot
    {
        /// <summary>Snapshots made at once.</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(2);

        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The snapshot's file name for a frame <paramref name="seconds"/> in, e.g. "Holiday (frame 1-23.450).jpg"
        /// (Windows names can't have ':').
        /// </summary>
        public static string NameFor(string videoName, double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            string time = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}-{t:mm\\-ss\\.fff}"
                : $"{(int)t.TotalMinutes}-{t:ss\\.fff}";
            return Path.GetFileNameWithoutExtension(videoName) + " (frame " + time + ").jpg";
        }

        /// <summary>
        /// Writes the frame <paramref name="seconds"/> into <paramref name="videoPath"/> to <paramref name="targetPath"/>,
        /// turned <paramref name="clockwise"/> degrees (0, 90, 180 or 270). Returns null when it worked, or why it didn't.
        /// </summary>
        public static async Task<string> MakeAsync(string videoPath, string targetPath, double seconds, int clockwise)
        {
            if (!File.Exists(SiteSettings.FfmpegPath))
                return "ffmpeg wasn't found at " + SiteSettings.FfmpegPath + ".";

            // -ss before -i: jumps straight there, and is exact to the frame because the frame is decoded
            string at = Math.Max(0, seconds).ToString("0.000", CultureInfo.InvariantCulture);
            string turn = clockwise == 0 ? "" : $"-vf \"{RotatedCopy.TurnFilter(clockwise)}\" ";
            string args = $"-hide_banner -loglevel error -nostdin -ss {at} -i \"{videoPath}\" -map 0:V:0 -frames:v 1 {turn}" +
                          $"-q:v 2 -c:v mjpeg -f image2 -y \"{targetPath}\"";

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = await VideoIndex.RunToolAsync(SiteSettings.FfmpegPath, args, Timeout).ConfigureAwait(false);

                // At the very end (paused after it finished) there's no frame from that time on: take the last one
                if (result.Error == null && !HasOutput(targetPath))
                {
                    args = $"-hide_banner -loglevel error -nostdin -sseof -1 -i \"{videoPath}\" -map 0:V:0 {turn}" +
                           $"-update 1 -q:v 2 -c:v mjpeg -f image2 -y \"{targetPath}\"";
                    result = await VideoIndex.RunToolAsync(SiteSettings.FfmpegPath, args, Timeout).ConfigureAwait(false);
                }

                if (result.Error == null && !HasOutput(targetPath))
                    result.Error = "ffmpeg didn't write a frame (is that past the end of the video?).";
                if (result.Error != null)
                    TryDelete(targetPath);
                return result.Error;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static bool HasOutput(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

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
