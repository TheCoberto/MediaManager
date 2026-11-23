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
            var arguments = $"{formatArgs} --no-playlist --no-part --no-mtime --ffmpeg-location \"{ffmpegDir}\" "
                + $"-o \"{template}\" -- \"{uri.AbsoluteUri}\"";

            // Whole request must fit inside httpRuntime executionTimeout="300", including an update and retry.
            var deadline = DateTime.UtcNow.AddSeconds(280);

            var result = RunYtDlp(arguments, outputDir, deadline);
            var produced = FindOutput(outputDir, ext);

            if ((result.ExitCode != 0 || produced == null) && TryUpdateYtDlp(deadline))
            {
                // Sites break old yt-dlp versions all the time; after a successful update, try once more.
                foreach (var f in Directory.GetFiles(outputDir)) { try { System.IO.File.Delete(f); } catch { } }
                result = RunYtDlp(arguments, outputDir, deadline);
                produced = FindOutput(outputDir, ext);
            }

            if (result.ExitCode == 0 && produced != null)
            {
                ActivityLog.Write(ActivityLog.Download, "Downloaded " + fileName + " ("
                    + ActivityLog.FormatBytes(new FileInfo(produced).Length) + ") from " + uri.AbsoluteUri);
                return File(produced, mimeType, fileName);
            }

            var error = LastErrorLine(result.Errors);
            ActivityLog.Error(ActivityLog.Download, "Couldn't download " + fileName + " from " + uri.AbsoluteUri
                + " (yt-dlp exit code " + result.ExitCode + "): " + error);
            try { Directory.Delete(outputDir, true); } catch { }

            return Content(failMessage + (error.Length > 0 ? " " + error : ""));
        }

        private static readonly object UpdateLock = new object();
        private static DateTime lastUpdateCheckUtc = DateTime.MinValue;

        /// <summary>
        /// Runs "yt-dlp -U" after a failed download, at most once an hour across all requests.
        /// Returns true only when a newer version was actually installed (so a retry is worth it).
        /// </summary>
        private static bool TryUpdateYtDlp(DateTime deadline)
        {
            lock (UpdateLock)
            {
                if (DateTime.UtcNow - lastUpdateCheckUtc < TimeSpan.FromHours(1))
                    return false;
                lastUpdateCheckUtc = DateTime.UtcNow;

                var update = RunYtDlp("-U", Path.GetTempPath(), deadline, captureOutput: true);
                var log = update.Output + update.Errors;
                if (update.ExitCode == 0 && log.IndexOf("Updated yt-dlp to", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ActivityLog.Write(ActivityLog.Download, "Updated yt-dlp after a failed download: " + LastLine(update.Output));
                    return true;
                }

                if (update.ExitCode != 0)
                    ActivityLog.Error(ActivityLog.Download, "Couldn't update yt-dlp (exit code " + update.ExitCode + "): "
                        + LastErrorLine(update.Errors.Length > 0 ? update.Errors : update.Output));
                return false;
            }
        }

        private class YtDlpResult
        {
            public int ExitCode;
            public string Output = "";
            public string Errors = "";
        }

        private static YtDlpResult RunYtDlp(string arguments, string workingDir, DateTime deadline, bool captureOutput = false)
        {
            var output = new StringBuilder();
            var errors = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = SiteSettings.YtDlpPath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new YtDlpResult { ExitCode = -1, Errors = "ERROR: Timed out." };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (s, e) => { if (captureOutput && e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit((int)remaining.TotalMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    lock (errors) errors.AppendLine("ERROR: Timed out.");
                }
                process.WaitForExit();

                lock (output) lock (errors)
                    return new YtDlpResult { ExitCode = process.ExitCode, Output = output.ToString(), Errors = errors.ToString() };
            }
        }

        private static string FindOutput(string outputDir, string ext)
        {
            return Directory.GetFiles(outputDir)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        private static string LastLine(string text)
        {
            return (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
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
