using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            return Download(name, url, ".mp3", "audio/mpeg", "-x --audio-format mp3", "Failed to download audio.");
        }

        public ActionResult DownloadVideo(string name, string url)
        {
            return Download(name, url, ".mp4", "video/mp4", "-f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/mp4\"", "Failed to download video.");
        }

        private ActionResult Download(string name, string url, string ext, string mimeType, string formatArgs, string failMessage)
        {
            // Only allow real http(s) links, so the "url" can't be used to pass extra options to yt-dlp.
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Content("Please enter a valid http(s) link.");

            var safeName = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            if (safeName.Length == 0)
                safeName = "download";

            var fileName = safeName + ext;
            var outputDir = Path.Combine(Path.GetTempPath(), "MediaManagerDownloads", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, fileName);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = SiteSettings.YtDlpPath,
                Arguments = $"{formatArgs} -o \"{outputPath}\" -- \"{uri.AbsoluteUri}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processStartInfo;
                process.Start();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && System.IO.File.Exists(outputPath))
                    return File(outputPath, mimeType, fileName);
            }

            return Content(failMessage);
        }
    }
}
