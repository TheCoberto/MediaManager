using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using AppConstants = MediaManager.Models.Constants.Constants;

namespace MediaManager.Models
{
    /// <summary>
    /// Small JPEG previews for the gallery, Favorites and Flagged pages: photos scaled down, and a frame from each
    /// video (its poster). Made on first request and kept in &lt;media root&gt;\.thumbs\ (one file per media file,
    /// named by a hash of its path), so the site needs write access to the media folder, as it does for uploads.
    ///
    /// JPEG / PNG are scaled here (System.Drawing); videos, WebP and HEIC go through ffmpeg (so HEIC photos get a
    /// preview even in browsers that can't show HEIC). GIFs aren't thumbnailed, so they keep moving. If a thumbnail
    /// can't be made (no ffmpeg, a damaged file, no write access), the page just gets the file itself.
    /// </summary>
    public static class Thumbnails
    {
        public const string FolderName = ".thumbs";

        // Fits the gallery's 500 x 400 box at 2x for sharp phone / high-DPI screens
        private const int MaxWidth = 1000;
        private const int MaxHeight = 800;
        private const long JpegQuality = 82;

        /// <summary>JPEG / PNG smaller than this are sent as they are: a thumbnail wouldn't save much.</summary>
        private const long SmallImageBytes = 150 * 1024;

        private static readonly string[] DrawingExtensions = { ".jpg", ".jpeg", ".png" };
        private static readonly string[] FfmpegImageExtensions = { ".webp", ".heic" };

        private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(30);

        /// <summary>Thumbnails made at once (each one can hold a full-size photo in memory or run ffmpeg).</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        /// <summary>
        /// The thumbnail URL for a file, or null if it doesn't get one (show the file itself). The URL changes when
        /// the file does, so browsers can keep thumbnails for a long time.
        /// </summary>
        public static string UrlFor(UrlHelper url, string folder, string fileName, FileInfo info = null)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            bool drawing = DrawingExtensions.Contains(ext);
            if (!drawing && !UsesFfmpeg(ext))
                return null;

            if (info == null)
            {
                string path = MediaPaths.ResolveFile(folder, fileName);
                if (path == null)
                    return null;
                info = new FileInfo(path);
            }

            try
            {
                if (!info.Exists || (drawing && info.Length < SmallImageBytes))
                    return null;
                if (!drawing && !File.Exists(SiteSettings.FfmpegPath))
                    return null;

                string version = info.LastWriteTimeUtc.Ticks.ToString("x") + "-" + info.Length.ToString("x");
                return url.Action("Get", "Thumbnails", new { folder = MediaPaths.NormalizeFolder(folder), file = fileName, v = version });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Physical path of the file's thumbnail, made now if it's missing or older than the file; null if it
        /// can't be made (send the file itself instead).
        /// </summary>
        public static async Task<string> GetOrCreateAsync(string sourcePath, string folder, string fileName)
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists)
                return null;

            string thumb = PathFor(folder, fileName);
            DateTime sourceTime = source.LastWriteTimeUtc;
            if (IsCurrent(thumb, sourceTime))
                return thumb;

            // Don't keep retrying a file that failed (until it changes, or a while has passed)
            string failKey = "ThumbFailed|" + thumb + "|" + sourceTime.Ticks + "|" + source.Length;
            if (MemoryCache.Default.Contains(failKey))
                return null;

            await Gate.WaitAsync().ConfigureAwait(false);
            string temp = null;
            try
            {
                if (IsCurrent(thumb, sourceTime)) // made by another request while this one waited
                    return thumb;

                Directory.CreateDirectory(Path.GetDirectoryName(thumb));
                temp = thumb + "." + Guid.NewGuid().ToString("N") + ".tmp";

                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                bool made = DrawingExtensions.Contains(ext)
                    ? MakeWithDrawing(sourcePath, temp)
                    : await MakeWithFfmpegAsync(sourcePath, temp, AppConstants.VideoExtensions.Contains(ext)).ConfigureAwait(false);

                if (!made || !File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    MemoryCache.Default.Set(failKey, true, DateTimeOffset.Now.Add(RetryFailedAfter));
                    return null;
                }

                // Stamped with the file's own time: a thumbnail is current while the two match
                File.SetLastWriteTimeUtc(temp, sourceTime);
                if (File.Exists(thumb))
                    File.Delete(thumb);
                File.Move(temp, thumb);
                temp = null;
                return thumb;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                                       || ex is OutOfMemoryException || ex is ExternalException || ex is InvalidOperationException
                                       || ex is System.ComponentModel.Win32Exception)
            {
                // A damaged / unsupported file (System.Drawing throws OutOfMemory or ArgumentException for those),
                // no write access to the media folder, or ffmpeg couldn't start
                Trace.TraceWarning("Thumbnail for {0} failed: {1}", sourcePath, ex.Message);
                MemoryCache.Default.Set(failKey, true, DateTimeOffset.Now.Add(RetryFailedAfter));
                return null;
            }
            finally
            {
                Gate.Release();
                if (temp != null)
                    TryDelete(temp);
            }
        }

        /// <summary>The file was renamed, moved, deleted or rewritten through the site: drop its thumbnail.</summary>
        public static void Forget(string folder, string fileName)
        {
            if (MediaPaths.IsPlainFileName(fileName))
                TryDelete(PathFor(folder, fileName));
        }

        private static bool UsesFfmpeg(string ext) =>
            FfmpegImageExtensions.Contains(ext) || AppConstants.VideoExtensions.Contains(ext);

        private static bool IsCurrent(string thumb, DateTime sourceTimeUtc)
        {
            try
            {
                var info = new FileInfo(thumb);
                return info.Exists && info.Length > 0 && info.LastWriteTimeUtc == sourceTimeUtc;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>.thumbs\ab\ab12...ef.jpg: a hash of "folder/file" (lower case, as Windows names aren't case-sensitive).</summary>
        private static string PathFor(string folder, string fileName)
        {
            string key = (MediaPaths.NormalizeFolder(folder) + "/" + fileName).ToLowerInvariant();
            string hash;
            using (var sha = SHA1.Create())
                hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2")));
            return Path.Combine(MediaPaths.Root, FolderName, hash.Substring(0, 2), hash + ".jpg");
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
                // in use or no access: it'll be replaced next time it's needed
            }
        }

        // ---------- JPEG / PNG ----------

        private const int ExifOrientation = 0x0112;

        private static bool MakeWithDrawing(string sourcePath, string target)
        {
            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false))
            {
                // Transparent images would get a solid background as JPEG: send those as they are
                if (Image.IsAlphaPixelFormat(image.PixelFormat) && Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Phones save photos sideways with a note to turn them (EXIF orientation); browsers follow it for the
                // original, so the thumbnail must be turned the same way.
                var turn = RotationFor(image);
                bool sideways = turn == RotateFlipType.Rotate90FlipNone || turn == RotateFlipType.Rotate90FlipX
                             || turn == RotateFlipType.Rotate270FlipNone || turn == RotateFlipType.Rotate270FlipX;

                int width = image.Width, height = image.Height;
                double scale = Math.Min(1.0, Math.Min(
                    MaxWidth / (double)(sideways ? height : width),
                    MaxHeight / (double)(sideways ? width : height)));
                int w = Math.Max(1, (int)Math.Round(width * scale));
                int h = Math.Max(1, (int)Math.Round(height * scale));

                using (var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    using (var attributes = new ImageAttributes())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY); // no faint border along the edges
                        g.DrawImage(image, new Rectangle(0, 0, w, h), 0, 0, width, height, GraphicsUnit.Pixel, attributes);
                    }

                    // Turned after scaling: much cheaper than turning the full-size photo
                    if (turn != RotateFlipType.RotateNoneFlipNone)
                        bitmap.RotateFlip(turn);

                    var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
                        bitmap.Save(target, jpeg, parameters);
                    }
                }
            }
            return true;
        }

        private static RotateFlipType RotationFor(Image image)
        {
            if (!image.PropertyIdList.Contains(ExifOrientation))
                return RotateFlipType.RotateNoneFlipNone;

            var value = image.GetPropertyItem(ExifOrientation).Value;
            switch (value != null && value.Length > 0 ? value[0] : 1)
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        // ---------- ffmpeg: videos, WebP, HEIC ----------

        private static async Task<bool> MakeWithFfmpegAsync(string sourcePath, string target, bool isVideo)
        {
            if (!File.Exists(SiteSettings.FfmpegPath))
                return false;

            // A video's very first frame is often black, so try one second in; very short videos don't have one.
            if (isVideo && await RunFfmpegAsync(sourcePath, target, "-ss 1").ConfigureAwait(false) && HasOutput(target))
                return true;

            return await RunFfmpegAsync(sourcePath, target, "").ConfigureAwait(false) && HasOutput(target);
        }

        private static bool HasOutput(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

        private static async Task<bool> RunFfmpegAsync(string sourcePath, string target, string seek)
        {
            // Fit inside the box without ever making it bigger; ffmpeg turns phone videos/photos the right way up itself
            string scale = $"scale='min({MaxWidth},iw)':'min({MaxHeight},ih)':force_original_aspect_ratio=decrease";
            string args = $"-hide_banner -loglevel error -nostdin {seek} -i \"{sourcePath}\" -frames:v 1 -vf \"{scale}\" " +
                          $"-q:v 4 -c:v mjpeg -f image2 -y \"{target}\"";

            var psi = new ProcessStartInfo
            {
                FileName = SiteSettings.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = Task.Run(() => process.WaitForExit((int)FfmpegTimeout.TotalMilliseconds));
                if (!await exited.ConfigureAwait(false))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { } // already gone
                    Trace.TraceWarning("Thumbnail for {0}: ffmpeg took too long", sourcePath);
                    return false;
                }

                string errors = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    Trace.TraceWarning("Thumbnail for {0}: ffmpeg failed: {1}", sourcePath, errors);
                return process.ExitCode == 0;
            }
        }
    }
}
