using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaManager.Models
{
    /// <summary>
    /// "Save rotated copy" on the View page: writes a turned copy of a picture or video next to the original, which
    /// is never changed. The angle is clockwise, relative to how the browser shows the file (so a phone photo or
    /// video that the browser already turns upright by its orientation note is turned from there).
    ///
    /// JPEG / PNG / still GIF: turned with System.Drawing and saved in the same format (JPEG at high quality), with
    /// the orientation note reset, so every viewer shows it the same way. Pictures Windows can't read (WebP, HEIC)
    /// are turned by ffmpeg into a JPEG.
    ///
    /// MP4 / MOV: no re-encoding. ffmpeg copies the video and audio as they are and only changes the rotation note
    /// players follow, so there's no quality loss and it's about as fast as copying the file. Other videos (WebM, AVI)
    /// are re-encoded to an H.264 MP4 with the picture itself turned.
    /// </summary>
    public static class RotatedCopy
    {
        /// <summary>Longest a video may take: a re-encode of a long video is slow.</summary>
        private static readonly TimeSpan VideoTimeout = TimeSpan.FromMinutes(15);

        /// <summary>Copies made at once (each reads and writes a whole file, or holds a full-size photo in memory).</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(2);

        private const long JpegQuality = 95;
        private const int ExifOrientation = 0x0112;

        /// <summary>Clockwise angle as 0, 90, 180 or 270 (any multiple of 90 in), or null if it isn't a quarter turn.</summary>
        public static int? Normalize(int degrees)
        {
            if (degrees % 90 != 0)
                return null;
            return ((degrees % 360) + 360) % 360;
        }

        /// <summary>
        /// The extension the copy gets: the original's, except pictures Windows can't write (WebP, HEIC → .jpg) and
        /// videos that have to be re-encoded (→ .mp4).
        /// </summary>
        public static string CopyExtension(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (MediaPaths.KindOf(fileName))
            {
                case MediaKind.Image:
                    return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" ? ext : ".jpg";
                case MediaKind.Video:
                    return ext == ".mp4" || ext == ".mov" ? ext : ".mp4";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Writes <paramref name="sourcePath"/> turned <paramref name="clockwise"/> degrees (90, 180 or 270) to
        /// <paramref name="targetPath"/>. Returns null when it worked, or why it didn't (nothing is left behind then).
        /// </summary>
        public static async Task<string> MakeAsync(string sourcePath, string targetPath, int clockwise)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string error = MediaPaths.KindOf(sourcePath) == MediaKind.Video
                    ? await MakeVideoAsync(sourcePath, targetPath, clockwise).ConfigureAwait(false)
                    : await MakeImageAsync(sourcePath, targetPath, clockwise).ConfigureAwait(false);

                if (error != null)
                    TryDelete(targetPath);
                else
                    File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath)); // sorts with the original by date
                return error;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(targetPath);
                return "Couldn't write the copy: " + ex.Message;
            }
            finally
            {
                Gate.Release();
            }
        }

        // ---------- pictures ----------

        private static async Task<string> MakeImageAsync(string sourcePath, string targetPath, int clockwise)
        {
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".gif")
                return await MakeImageWithFfmpegAsync(sourcePath, targetPath, clockwise).ConfigureAwait(false);

            try
            {
                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true))
                {
                    if (image.RawFormat.Guid == ImageFormat.Gif.Guid && image.GetFrameCount(FrameDimension.Time) > 1)
                        return "Animated GIFs can't be rotated (only the first frame would be kept).";

                    // Upright first, the way the browser shows it, then the turn asked for
                    var upright = Thumbnails.RotationFor(image);
                    if (upright != RotateFlipType.RotateNoneFlipNone)
                        image.RotateFlip(upright);
                    image.RotateFlip(TurnFor(clockwise));
                    ResetOrientation(image);

                    if (ext == ".png")
                        image.Save(targetPath, ImageFormat.Png);
                    else if (ext == ".gif")
                        image.Save(targetPath, ImageFormat.Gif);
                    else
                    {
                        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
                            image.Save(targetPath, jpeg, parameters);
                        }
                    }
                }
                return null;
            }
            catch (Exception ex) when (ex is OutOfMemoryException || ex is ArgumentException || ex is ExternalException)
            {
                // Windows can't read it (often not really the format its name says, e.g. a HEIC called ".jpg"): ffmpeg usually can
                TryDelete(targetPath);
                return await MakeImageWithFfmpegAsync(sourcePath, targetPath, clockwise).ConfigureAwait(false);
            }
        }

        private static RotateFlipType TurnFor(int clockwise)
        {
            switch (clockwise)
            {
                case 90: return RotateFlipType.Rotate90FlipNone;
                case 180: return RotateFlipType.Rotate180FlipNone;
                case 270: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        /// <summary>The picture is upright now, so its orientation note (if it has one) must say "as stored".</summary>
        private static void ResetOrientation(Image image)
        {
            if (!image.PropertyIdList.Contains(ExifOrientation))
                return;
            var item = image.GetPropertyItem(ExifOrientation);
            item.Type = 3; // SHORT
            item.Len = 2;
            item.Value = new byte[] { 1, 0 };
            image.SetPropertyItem(item);
        }

        /// <summary>WebP, HEIC and pictures Windows can't read: ffmpeg turns them into a high-quality JPEG.</summary>
        private static async Task<string> MakeImageWithFfmpegAsync(string sourcePath, string targetPath, int clockwise)
        {
            if (!File.Exists(SiteSettings.FfmpegPath))
                return "ffmpeg wasn't found at " + SiteSettings.FfmpegPath + ".";

            string args = $"-hide_banner -loglevel error -nostdin -i \"{sourcePath}\" -frames:v 1 -vf \"{TurnFilter(clockwise)}\" " +
                          $"-q:v 2 -c:v mjpeg -f image2 -y \"{targetPath}\"";
            var result = await VideoIndex.RunToolAsync(SiteSettings.FfmpegPath, args).ConfigureAwait(false);
            if (result.Error != null)
                return result.Error;
            return File.Exists(targetPath) && new FileInfo(targetPath).Length > 0 ? null : "ffmpeg didn't write the copy.";
        }

        /// <summary>ffmpeg filter that turns the picture clockwise.</summary>
        private static string TurnFilter(int clockwise)
        {
            switch (clockwise)
            {
                case 90: return "transpose=clock";
                case 180: return "hflip,vflip";
                case 270: return "transpose=cclock";
                default: return "null";
            }
        }

        // ---------- videos ----------

        private static async Task<string> MakeVideoAsync(string sourcePath, string targetPath, int clockwise)
        {
            if (!File.Exists(SiteSettings.FfmpegPath))
                return "ffmpeg wasn't found at " + SiteSettings.FfmpegPath + ".";
            if (!File.Exists(SiteSettings.FfprobePath))
                return "ffprobe wasn't found at " + SiteSettings.FfprobePath + " (it checks the copy).";

            string ext = Path.GetExtension(targetPath).ToLowerInvariant();
            bool lossless = ext == ".mp4" || ext == ".mov";
            int? expectedRotation = null;
            string args;

            if (lossless)
            {
                // Players turn the video by the rotation note (counter-clockwise degrees in ffmpeg's terms). The new
                // note replaces the old one, so it's the old turn plus this one.
                int current = await ProbeRotationAsync(sourcePath).ConfigureAwait(false) ?? 0;
                int rotation = ((current - clockwise) % 360 + 360) % 360;
                if (rotation > 180) rotation -= 360; // -90 rather than 270, as phones write it
                expectedRotation = rotation;

                args = $"-hide_banner -loglevel error -nostdin -display_rotation:v:0 {rotation} -i \"{sourcePath}\" " +
                       $"-map 0:V -map 0:a? -c copy -map_metadata 0 -movflags +faststart -f {ext.TrimStart('.')} -y \"{targetPath}\"";
            }
            else
            {
                // ffmpeg turns the picture upright by its rotation note as it decodes, then the turn asked for
                args = $"-hide_banner -loglevel error -nostdin -i \"{sourcePath}\" -map 0:V:0 -map 0:a? -vf \"{TurnFilter(clockwise)}\" " +
                       $"-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -c:a aac -b:a 192k -map_metadata 0 " +
                       $"-movflags +faststart -f mp4 -y \"{targetPath}\"";
            }

            var result = await VideoIndex.RunToolAsync(SiteSettings.FfmpegPath, args, VideoTimeout).ConfigureAwait(false);
            if (result.Error != null)
                return result.Error;

            return await CheckVideoAsync(sourcePath, targetPath, expectedRotation).ConfigureAwait(false);
        }

        /// <summary>Checks the copy before it's kept: it has the original's length, and (no re-encode) the new rotation note.</summary>
        private static async Task<string> CheckVideoAsync(string sourcePath, string targetPath, int? expectedRotation)
        {
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                return "ffmpeg didn't write the copy.";

            double? copyLength = await VideoIndex.ProbeLengthAsync(targetPath).ConfigureAwait(false);
            if (copyLength == null || copyLength <= 0)
                return "ffprobe couldn't read a length from the copy, so it wasn't kept.";
            double? originalLength = await VideoIndex.ProbeLengthAsync(sourcePath).ConfigureAwait(false);
            if (originalLength != null && !VideoIndex.Close(copyLength.Value, originalLength.Value))
                return "The copy came out a different length from the original, so it wasn't kept.";

            if (expectedRotation != null)
            {
                int written = await ProbeRotationAsync(targetPath).ConfigureAwait(false) ?? 0;
                if (((written - expectedRotation.Value) % 360 + 360) % 360 != 0)
                    return "ffmpeg didn't write the new rotation into the copy, so it wasn't kept.";
            }
            return null;
        }

        /// <summary>The video's rotation note in counter-clockwise degrees (phones write -90 for portrait), or null if it has none.</summary>
        private static async Task<int?> ProbeRotationAsync(string path)
        {
            var result = await VideoIndex.RunToolAsync(SiteSettings.FfprobePath,
                $"-v error -select_streams V:0 -show_entries stream_side_data=rotation -of default=noprint_wrappers=1:nokey=1 \"{path}\"")
                .ConfigureAwait(false);
            if (result.Error != null)
                return null;

            foreach (string line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees))
                    return (int)Math.Round(degrees);
            return null;
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
