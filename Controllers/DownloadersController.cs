using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using MediaManager.Filters;
using MediaManager.Models;

namespace MediaManager.Controllers
{
    [AuthFilter]
    [Feature("Downloaders", enabledByDefault: true)]
    public class DownloadersController : Controller
    {
        public ActionResult AudioDownloader()
        {
            return View();
        }

        public ActionResult VideoDownloader()
        {
            return View();
        }

        public ActionResult DownloadAudio(string name, string url)
        {
            return Download(name, url, ".mp3", "audio/mpeg",
                "-f \"ba/b\" -x --audio-format mp3", "Failed to download audio.");
        }

        public ActionResult DownloadVideo(string name, string url)
        {
            // Prefer mp4+m4a (no re-encode needed), but fall back to any streams rather than failing
            // when a site has no mp4 version; ffmpeg merges/remuxes the result into an .mp4.
            return Download(name, url, ".mp4", "video/mp4",
                "-f \"bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b\" --merge-output-format mp4 --remux-video mp4",
                "Failed to download video.");
        }

        private ActionResult Download(string name, string url, string ext, string mimeType, string formatArgs, string failMessage)
        {
            // Only allow real http(s) links, so the "url" can't be used to pass extra options to yt-dlp.
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Content("Please enter a valid http(s) link.");

            if (!System.IO.File.Exists(SiteSettings.YtDlpPath))
            {
                ActivityLog.Error(ActivityLog.Download, "yt-dlp.exe not found at " + SiteSettings.YtDlpPath + ".");
                return Content(failMessage + " (yt-dlp is not installed on the server.)");
            }

            var safeName = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (safeName.Length == 0)
                safeName = "download";

            var fileName = safeName + ext;
            var outputDir = Path.Combine(Path.GetTempPath(), "MediaManagerDownloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);

            // Let yt-dlp pick the extension (a fixed ".mp3"/".mp4" in the template breaks when it has to
            // convert or merge), then take whatever file it produced.
            var template = Path.Combine(outputDir, "media.%(ext)s");
            var ffmpegDir = Path.GetDirectoryName(SiteSettings.FfmpegPath);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = SiteSettings.YtDlpPath,
                Arguments = $"{formatArgs} --no-playlist --no-part --no-mtime --ffmpeg-location \"{ffmpegDir}\" "
                    + $"-o \"{template}\" -- \"{uri.AbsoluteUri}\"",
                WorkingDirectory = outputDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var errors = new StringBuilder();
            int exitCode;
            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.OutputDataReceived += (s, e) => { };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Stay inside the request timeout (httpRuntime executionTimeout="300").
                if (!process.WaitForExit(280 * 1000))
                {
                    try { process.Kill(); } catch { }
                    lock (errors) errors.AppendLine("ERROR: Timed out.");
                }
                process.WaitForExit();
                exitCode = process.ExitCode;
            }

            var produced = Directory.GetFiles(outputDir)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            if (exitCode == 0 && produced != null)
            {
                ActivityLog.Write(ActivityLog.Download, "Downloaded " + fileName + " ("
                    + ActivityLog.FormatBytes(new FileInfo(produced).Length) + ") from " + uri.AbsoluteUri);
                return File(produced, mimeType, fileName);
            }

            string error;
            lock (errors) error = LastErrorLine(errors.ToString());
            ActivityLog.Error(ActivityLog.Download, "Couldn't download " + fileName + " from " + uri.AbsoluteUri
                + " (yt-dlp exit code " + exitCode + "): " + error);
            try { Directory.Delete(outputDir, true); } catch { }

            return Content(failMessage + (error.Length > 0 ? " " + error : ""));
        }

        private static string LastErrorLine(string stderr)
        {
            var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var line = lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                ?? lines.LastOrDefault() ?? "";
            line = line.Trim();
            return line.Length > 500 ? line.Substring(0, 500) : line;
        }
    }
}
